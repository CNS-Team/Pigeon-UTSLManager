using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Pigeon.Manager.Desktop;

public sealed partial class MainWindow {
    sealed class ArtifactChoice(PluginArtifact artifact, bool available, Action<ArtifactChoice, bool> changed) : INotifyPropertyChanged {
        public PluginArtifact Artifact { get; } = artifact;
        public bool Available { get; } = available;
        bool selected, editable;
        public bool Selected { get => selected; set { if (selected == value) return; selected = value; Notify(); changed(this, value); } }
        public bool Editable { get => editable; set { if (editable == value) return; editable = value; Notify(); } }
        public void Select(bool value) { if (selected == value) return; selected = value; Notify(nameof(Selected)); }
        public event PropertyChangedEventHandler? PropertyChanged;
        void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }
    PluginArtifact[] libraryArtifacts = [];
    EngineArtifact[] libraryEngines = [];
    ArtifactChoice[] artifactChoices = [];
    readonly Dictionary<string, PluginArtifact> runtimePlugins = new(StringComparer.OrdinalIgnoreCase);
    bool libraryInitialized, libraryBusy, libraryLoading, runtimeLoading, runtimeLoaded, runtimeDirty, applyingRuntime, libraryTargetsPending;
    bool selectionConflicts, missingSelectedArtifact, missingSelectedEngine;
    bool? lastArtifactWritable;
    string? runtimeTargetId, runtimeEngineRevision;
    int catalogGeneration, targetGeneration;
    string? PluginTargetId => (PluginTarget.SelectedItem as ComboBoxItem)?.Tag as string;
    ServerState? RuntimeTarget => states.FirstOrDefault(s => s.Profile.Id == PluginTargetId);
    bool RuntimeStopped => RuntimeTarget is { Identity: null, State: "STOPPED" };
    bool RuntimeWritable => runtimeLoaded && runtimeTargetId == PluginTargetId && RuntimeStopped
        && !libraryBusy && !runtimeLoading && !libraryLoading && !busy && startupQueue?.Active != true && stopQueue?.Active != true;
    static string ArtifactKey(PluginArtifact artifact) => artifact.FileName + "|" + artifact.Sha256;
    static bool ArtifactConflict(PluginArtifact a, PluginArtifact b) => string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
    bool SelectionConflicts => selectionConflicts;
    bool MissingSelectedArtifact => missingSelectedArtifact;
    bool MissingSelectedEngine => missingSelectedEngine;
    static string BytesLabel(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576d:N1} MB" : $"{bytes / 1024d:N1} KB";

    void InitializeLibrary() {
        if (libraryInitialized) return;
        libraryInitialized = true;
        ArtifactList.ItemTemplate = new FuncDataTemplate<ArtifactChoice>((row, _) => {
            if (row == null) return new TextBlock();
            var info = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
            var title = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
            title.Children.Add(new TextBlock { Text = row.Artifact.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var version = new TextBlock { Text = row.Artifact.Version, Foreground = Brush.Parse("#B4C4F2"), FontSize = 11 };
            Grid.SetColumn(version, 1); title.Children.Add(version); info.Children.Add(title);
            info.Children.Add(new TextBlock { Text = row.Artifact.FileName + "  ·  " + BytesLabel(row.Artifact.Bytes), Foreground = Brush.Parse("#989CAB"), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock { Text = "SHA256 " + row.Artifact.Sha256, Foreground = Brush.Parse("#8F94A5"), FontFamily = FontFamily.Parse("Cascadia Mono,Consolas"), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!row.Available) info.Children.Add(new TextBlock { Text = "此精确版本不在总库中，请重新收录或取消勾选", Foreground = Brush.Parse("#E7AB98"), FontSize = 10 });
            var toggle = new CheckBox { Content = info, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(3,4) };
            toggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ArtifactChoice.Selected)) { Source = row, Mode = BindingMode.TwoWay });
            toggle.Bind(IsEnabledProperty, new Binding(nameof(ArtifactChoice.Editable)) { Source = row });
            ToolTip.SetTip(toggle, row.Artifact.FileName + "\n" + row.Artifact.Sha256);
            return toggle;
        });
        Closed += (_, _) => { catalogGeneration++; targetGeneration++; };
    }
    // HostClient serializes/deserializes IPC payloads on this worker as well.
    Task<Reply> LibraryRequest(Request request, int timeoutMs = 30_000) => Task.Run(async () => {
        var reply = await client.Send(request, timeoutMs).ConfigureAwait(false);
        if (!reply.Ok) throw new IOException(string.IsNullOrWhiteSpace(reply.Message) ? "后台拒绝此操作" : reply.Message);
        return reply;
    });
    void LibraryMessage(string message, bool error = false) {
        PluginStatus.Text = message;
        PluginStatus.Foreground = Brush.Parse(error ? "#F0AF9A" : "#AFBCD9");
        StatusText.Text = message;
    }
    void InvalidatePreview() { RuntimePreviewPath.Text = ""; RuntimePreviewPath.IsVisible = false; }
    void MarkRuntimeDirty() {
        if (applyingRuntime) return;
        ValidateRuntimeChoices();
        runtimeDirty = true; InvalidatePreview(); UpdateLibraryControls();
    }
    void ValidateRuntimeChoices() {
        selectionConflicts = runtimePlugins.Values.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)
            || runtimePlugins.Values.GroupBy(p => p.FileName, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
        var keys = libraryArtifacts.Select(ArtifactKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        missingSelectedArtifact = runtimePlugins.Keys.Any(key => !keys.Contains(key));
        missingSelectedEngine = runtimeEngineRevision != null && !libraryEngines.Any(e => e.Revision == runtimeEngineRevision);
    }
    void UpdateLibraryControls() {
        if (!libraryInitialized) return;
        bool writable = RuntimeWritable;
        bool idle = !libraryBusy && !libraryLoading && !runtimeLoading;
        PluginTarget.IsEnabled = !libraryBusy;
        RuntimeEnabled.IsEnabled = writable;
        EngineVersion.IsEnabled = writable;
        SaveRuntimeButton.IsEnabled = writable && !SelectionConflicts && !MissingSelectedArtifact && !MissingSelectedEngine;
        PreviewRuntimeButton.IsEnabled = writable && !runtimeDirty && RuntimeEnabled.IsChecked == true && !SelectionConflicts && !MissingSelectedArtifact && !MissingSelectedEngine;
        ImportCurrentPluginsButton.IsEnabled = ImportCurrentEngineButton.IsEnabled = writable;
        ImportSharedPluginsButton.IsEnabled = RefreshLibraryButton.IsEnabled = idle;
        AddLegacyPluginsButton.IsEnabled = idle;
        ApplyLegacyPluginsButton.IsEnabled = writable && PluginList.SelectedItems?.Count > 0;
        if (lastArtifactWritable != writable) {
            lastArtifactWritable = writable;
            foreach (var choice in artifactChoices) choice.Editable = writable;
        }
        var target = RuntimeTarget;
        RuntimeTargetStatus.Text = target == null ? "请选择目标服务器"
            : runtimeLoading ? "正在载入此服已保存的选择…"
            : !runtimeLoaded ? "配置未载入，操作不可用；请刷新重试"
            : target.Identity != null || target.State != "STOPPED" ? StateName(target.State) + " · 配置只读，停止后可修改"
            : startupQueue?.Active == true || stopQueue?.Active == true ? "启停队列运行中 · 配置只读"
            : runtimeDirty ? "已停止 · 有未保存的更改"
            : "已停止 · 已载入保存的配置";
        RuntimeSelectionCount.Text = runtimeLoaded ? $"已选 {runtimePlugins.Count} 个精确插件版本"
            + (SelectionConflicts ? " · 同名版本冲突，请重新勾选" : MissingSelectedArtifact ? " · 有版本未收录" : "") : "尚未载入插件选择";
        SaveRuntimeButton.Content = runtimeDirty ? "保存此服运行配置 · 未保存" : "保存此服运行配置";
        var engine = libraryEngines.FirstOrDefault(e => e.Revision == runtimeEngineRevision);
        EngineDescription.Text = runtimeEngineRevision == null ? "使用目标服原有核心"
            : engine == null ? "已保存的核心版本未在库中找到：" + runtimeEngineRevision
            : engine.Name + " · " + engine.FileCount + " 个文件 · " + BytesLabel(engine.Bytes) + "\nRevision " + engine.Revision;
    }
    void RenderArtifacts() {
        ValidateRuntimeChoices();
        var available = libraryArtifacts.ToDictionary(ArtifactKey, StringComparer.OrdinalIgnoreCase);
        artifactChoices = libraryArtifacts.Concat(runtimePlugins.Where(p => !available.ContainsKey(p.Key)).Select(p => p.Value))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Version, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Sha256, StringComparer.Ordinal)
            .Select(p => { var row = new ArtifactChoice(p, available.ContainsKey(ArtifactKey(p)), ArtifactSelected); row.Select(runtimePlugins.ContainsKey(ArtifactKey(p))); return row; }).ToArray();
        lastArtifactWritable = null; FilterArtifacts(); UpdateLibraryControls();
    }
    void FilterArtifacts() {
        string filter = LibrarySearch.Text?.Trim() ?? "";
        var rows = artifactChoices.Where(row => filter.Length == 0 || new[] { row.Artifact.Name, row.Artifact.FileName, row.Artifact.Version, row.Artifact.Sha256 }
            .Any(value => value.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
        ArtifactList.ItemsSource = rows;
        ArtifactCount.Text = filter.Length == 0 ? $"{libraryArtifacts.Length} 个版本" : $"显示 {rows.Length} / {artifactChoices.Length} 个版本";
        LibraryEmptyHint.IsVisible = rows.Length == 0;
        LibraryEmptyHint.Text = filter.Length == 0 ? "总库暂无插件，请收录 DLL 或导入目标服现有插件" : "没有匹配的插件版本";
    }
    void ArtifactSelected(ArtifactChoice choice, bool selected) {
        if (!RuntimeWritable) { choice.Select(runtimePlugins.ContainsKey(ArtifactKey(choice.Artifact))); return; }
        if (selected) {
            foreach (var entry in runtimePlugins.Where(p => ArtifactConflict(p.Value, choice.Artifact)).ToArray()) runtimePlugins.Remove(entry.Key);
            runtimePlugins[ArtifactKey(choice.Artifact)] = choice.Artifact;
        } else runtimePlugins.Remove(ArtifactKey(choice.Artifact));
        foreach (var row in artifactChoices) row.Select(runtimePlugins.ContainsKey(ArtifactKey(row.Artifact)));
        MarkRuntimeDirty();
    }
    void RebuildEngines() {
        ValidateRuntimeChoices();
        applyingRuntime = true;
        try {
            var entries = new List<ComboBoxItem> { new() { Content = "使用原服核心", Tag = null } };
            foreach (var engine in libraryEngines) {
                string revision = engine.Revision.Length > 12 ? engine.Revision[..12] : engine.Revision;
                var entry = new ComboBoxItem { Content = engine.Name + " · " + engine.Version + " · " + revision, Tag = engine.Revision };
                ToolTip.SetTip(entry, "Revision " + engine.Revision + "\n" + engine.Directory); entries.Add(entry);
            }
            if (MissingSelectedEngine) entries.Add(new() { Content = "未收录 · " + runtimeEngineRevision, Tag = runtimeEngineRevision });
            EngineVersion.ItemsSource = entries;
            EngineVersion.SelectedItem = entries.FirstOrDefault(e => (string?)e.Tag == runtimeEngineRevision) ?? entries[0];
        } finally { applyingRuntime = false; }
    }
    async Task LoadPlugins(bool discardDraft = false) {
        InitializeLibrary();
        if (libraryBusy) return;
        int generation = ++catalogGeneration;
        libraryLoading = true; UpdateLibraryControls(); LibraryMessage("正在载入总插件库与核心版本…");
        try {
            var catalog = LibraryRequest(new("shared-library-list"));
            var engines = LibraryRequest(new("engine-list"));
            await Task.WhenAll(catalog, engines);
            if (generation != catalogGeneration) return;
            libraryArtifacts = (await catalog).Artifacts ?? throw new IOException("总插件库响应缺少 Artifacts");
            libraryEngines = (await engines).Engines ?? throw new IOException("核心库响应缺少 Engines");
            RebuildEngines(); RenderArtifacts();
            if (discardDraft || !runtimeLoaded || !runtimeDirty || runtimeTargetId != PluginTargetId) await LoadRuntimeTarget();
            else LibraryMessage("已刷新总库；此服未保存的选择已保留");
            if (LegacyLibraryExpander.IsExpanded) await LoadLegacyPlugins();
        } catch (Exception e) { if (generation == catalogGeneration) LibraryMessage("载入失败：" + e.Message, true); }
        finally { if (generation == catalogGeneration) { libraryLoading = false; UpdateLibraryControls(); } }
    }
    async Task LoadRuntimeTarget() {
        InitializeLibrary();
        string? id = PluginTargetId;
        int generation = ++targetGeneration;
        runtimeTargetId = id; runtimeLoaded = runtimeDirty = false; runtimeLoading = id != null;
        runtimePlugins.Clear(); runtimeEngineRevision = null;
        applyingRuntime = true; RuntimeEnabled.IsChecked = false; applyingRuntime = false;
        InvalidatePreview(); RebuildEngines(); RenderArtifacts();
        if (id == null) { LibraryMessage("选择目标服务器后可查看和配置独立沙箱"); return; }
        LibraryMessage("正在载入目标服配置…");
        try {
            var reply = await LibraryRequest(new("runtime-selection", id));
            if (generation != targetGeneration || PluginTargetId != id) return;
            ApplyRuntimeSelection(reply.RuntimeSelection ?? throw new IOException("后台响应缺少 RuntimeSelection"));
            LibraryMessage("已载入目标服配置；收录新版本会保留总库中的旧版本");
        } catch (Exception e) { if (generation == targetGeneration) LibraryMessage("配置载入失败：" + e.Message, true); }
        finally { if (generation == targetGeneration) { runtimeLoading = false; UpdateLibraryControls(); } }
    }
    void ApplyRuntimeSelection(RuntimeSelection selection) {
        applyingRuntime = true;
        try {
            runtimePlugins.Clear();
            foreach (var artifact in selection.Plugins) runtimePlugins[ArtifactKey(artifact)] = artifact;
            RuntimeEnabled.IsChecked = selection.Enabled; runtimeEngineRevision = selection.EngineRevision;
            runtimeLoaded = true; runtimeDirty = false; InvalidatePreview(); RebuildEngines(); RenderArtifacts();
        } finally { applyingRuntime = false; }
    }
    async Task<bool> BeginLibraryOperation(string pending, bool targetRequired) {
        if (libraryBusy || libraryLoading || runtimeLoading) return false;
        if (targetRequired && !RuntimeWritable) { LibraryMessage("目标服须已停止且配置已载入，运行中仅可查看", true); return false; }
        libraryBusy = true; UpdateLibraryControls(); LibraryMessage(pending);
        // Refresh process state before requesting a write; backend remains authoritative.
        if (targetRequired) {
            try {
                var state = await LibraryRequest(new("list"));
                if (state.Servers?.FirstOrDefault(s => s.Profile.Id == runtimeTargetId) is not { Identity: null, State: "STOPPED" }
                    || state.StartupQueue?.Active == true || state.StopQueue?.Active == true)
                    throw new IOException("目标服状态已变化或启停队列正在运行，请停止后重试");
            } catch (Exception e) { EndLibraryOperation(); LibraryMessage(e.Message, true); return false; }
        }
        return true;
    }
    void EndLibraryOperation() {
        libraryBusy = false;
        if (libraryTargetsPending) { libraryTargetsPending = false; BuildTargets(); }
        UpdateLibraryControls();
    }
    async void RefreshLibraryClick(object? sender, RoutedEventArgs e) {
        if (runtimeDirty && !await Confirm("重新载入配置", "刷新将重新载入目标服已保存的选择，放弃此页未保存的更改。")) return;
        await LoadPlugins(discardDraft: true);
    }
    async void PluginTargetChanged(object? sender, SelectionChangedEventArgs e) {
        if (workspace == null || rebuilding || libraryBusy) return;
        await LoadRuntimeTarget();
    }
    void LibrarySearchChanged(object? sender, TextChangedEventArgs e) { if (libraryInitialized) FilterArtifacts(); }
    void RuntimeEnabledChanged(object? sender, RoutedEventArgs e) { if (libraryInitialized && !applyingRuntime && RuntimeWritable) MarkRuntimeDirty(); }
    void EngineVersionChanged(object? sender, SelectionChangedEventArgs e) {
        if (!libraryInitialized || applyingRuntime || !RuntimeWritable) return;
        runtimeEngineRevision = (EngineVersion.SelectedItem as ComboBoxItem)?.Tag as string; MarkRuntimeDirty();
    }
    async void ImportSharedPluginsClick(object? sender, RoutedEventArgs e) {
        if (!await BeginLibraryOperation("选择待收录的 DLL；新版本将追加到总库…", false)) return;
        try {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "收录插件版本（保留旧版本）", AllowMultiple = true, FileTypeFilter = [new FilePickerFileType("插件程序集") { Patterns = ["*.dll"] }] });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
            if (paths.Length == 0) { LibraryMessage("已取消收录，配置保持不变"); return; }
            var reply = await LibraryRequest(new("shared-library-import", Files: paths), 600_000);
            libraryArtifacts = reply.Artifacts ?? throw new IOException("收录响应缺少 Artifacts");
            RenderArtifacts(); LibraryMessage(reply.Message);
        } catch (Exception ex) { LibraryMessage("收录失败：" + ex.Message, true); }
        finally { EndLibraryOperation(); }
    }
    async void SaveRuntimeClick(object? sender, RoutedEventArgs e) {
        if (!SaveRuntimeButton.IsEnabled || !await BeginLibraryOperation("保存目标服的核心与插件版本选择…", true)) return;
        string? id = runtimeTargetId; int generation = targetGeneration;
        try {
            var selection = new RuntimeSelection(RuntimeEnabled.IsChecked == true, runtimePlugins.Values.ToArray()) { EngineRevision = runtimeEngineRevision };
            var reply = await Task.Run(async () => await LibraryRequest(new("runtime-selection-save", id, Json.Encode(selection))));
            if (generation != targetGeneration) return;
            ApplyRuntimeSelection(reply.RuntimeSelection ?? selection); LibraryMessage(reply.Message);
        } catch (Exception ex) { LibraryMessage("保存失败：" + ex.Message, true); }
        finally { EndLibraryOperation(); }
    }
    async void ImportCurrentPluginsClick(object? sender, RoutedEventArgs e) {
        string? id = PluginTargetId;
        if (!RuntimeWritable || !await Confirm("收录原服插件", $"{RuntimeTarget?.Profile.Name}\n将原服插件收录到总库，并用其精确版本初始化此服选择。此操作将保存选择并保持沙箱关闭。")) return;
        if (PluginTargetId != id || !await BeginLibraryOperation("收录原服插件并初始化版本选择…", true)) return;
        try {
            var reply = await LibraryRequest(new("runtime-import-current", id), 600_000);
            libraryArtifacts = reply.Artifacts ?? throw new IOException("导入响应缺少 Artifacts");
            ApplyRuntimeSelection(reply.RuntimeSelection ?? throw new IOException("导入响应缺少 RuntimeSelection"));
            LibraryMessage(reply.Message);
        } catch (Exception ex) { LibraryMessage("原服插件收录失败：" + ex.Message, true); }
        finally { EndLibraryOperation(); }
    }
    async void ImportCurrentEngineClick(object? sender, RoutedEventArgs e) {
        string? id = PluginTargetId;
        if (!RuntimeWritable || !await BeginLibraryOperation("收录停止服的核心与代码依赖…", true)) return;
        try {
            var reply = await LibraryRequest(new("engine-import-current", id), 600_000);
            libraryEngines = reply.Engines ?? throw new IOException("核心收录响应缺少 Engines");
            RebuildEngines(); LibraryMessage(reply.Message + "；可在核心列表中手动选择收录的版本");
        } catch (Exception ex) { LibraryMessage("核心收录失败：" + ex.Message, true); }
        finally { EndLibraryOperation(); }
    }
    async void PreviewRuntimeClick(object? sender, RoutedEventArgs e) {
        if (!PreviewRuntimeButton.IsEnabled || !await BeginLibraryOperation("生成并校验已保存的运行沙箱…", true)) return;
        try {
            var reply = await LibraryRequest(new("runtime-preview", runtimeTargetId), 600_000);
            if (string.IsNullOrWhiteSpace(reply.Path)) throw new IOException("后台未返回沙箱目录");
            RuntimePreviewPath.Text = reply.Path; RuntimePreviewPath.IsVisible = true;
            LibraryMessage(reply.Message + "；路径已显示，尚未启动服务端");
        } catch (Exception ex) { LibraryMessage("沙箱预览失败：" + ex.Message, true); }
        finally { EndLibraryOperation(); }
    }
    async void LegacyLibraryExpanding(object? sender, CancelRoutedEventArgs e) { if (workspace != null) await LoadLegacyPlugins(); }
    async Task LoadLegacyPlugins() {
        try {
            var reply = await LibraryRequest(new("plugins"));
            PluginCount.Text = $"旧复制库 · {reply.Plugins!.Length} 个插件";
            PluginList.ItemsSource = reply.Plugins.Select(p => {
                var text = new TextBlock { Text = p.Name + " · " + p.Version + " · " + p.FileName + " · SHA " + p.Sha256[..Math.Min(12,p.Sha256.Length)], FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(text, p.Sha256); return new ListBoxItem { Content = text, Tag = p.FileName };
            }).ToArray();
            UpdateLibraryControls();
        } catch (Exception ex) { LegacyPluginStatus.Text = "旧库载入失败：" + ex.Message; }
    }
    async void AddPluginsClick(object? sender, RoutedEventArgs e) {
        if (!await BeginLibraryOperation("选择导入旧复制库的 DLL…", false)) return;
        try {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "旧复制模式 · 导入 DLL", AllowMultiple = true, FileTypeFilter = [new FilePickerFileType("程序集") { Patterns = ["*.dll"] }] });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray(); if (paths.Length == 0) return;
            var reply = await LibraryRequest(new("library-add", Files: paths), 600_000);
            LegacyPluginStatus.Text = reply.Message; await LoadLegacyPlugins();
        } catch (Exception ex) { LegacyPluginStatus.Text = "旧库导入失败：" + ex.Message; }
        finally { EndLibraryOperation(); }
    }
    async void ApplyPluginsClick(object? sender, RoutedEventArgs e) {
        string? id = PluginTargetId;
        string[] files = PluginList.SelectedItems?.Cast<ListBoxItem>().Select(i => (string)i.Tag!).ToArray() ?? [];
        if (!RuntimeWritable || files.Length == 0) { LegacyPluginStatus.Text = "请选择旧库插件和已停止的目标服"; return; }
        if (!await Confirm("兼容模式：复制到原服", $"{RuntimeTarget?.Profile.Name}\n{string.Join('\n', files)}\n备份并复制到原服插件目录；独立沙箱选择保持不变。")) return;
        if (PluginTargetId != id || !await BeginLibraryOperation("备份并复制旧库插件到原服…", true)) return;
        try { var reply = await LibraryRequest(new("apply-plugins", id, Files: files), 600_000); LegacyPluginStatus.Text = reply.Message; }
        catch (Exception ex) { LegacyPluginStatus.Text = "复制失败：" + ex.Message; }
        finally { EndLibraryOperation(); }
    }
}
