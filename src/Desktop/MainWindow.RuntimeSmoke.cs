using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Pigeon.Manager.Desktop;

public sealed partial class MainWindow {
    // Opt-in extension to the existing marked, empty-workspace Probe fixture.
    // No production roster, external directory or non-fixture process is accepted.
    async Task RuntimeLibrarySmoke(Action<string, bool> check, Func<string, Task> snap, string output, string[] fixtureIds) {
        void Check(string name, bool pass) => check("runtime-ui/" + name, pass);
        void RequireFixtures() {
            var profiles = workspace.Load();
            if (!File.Exists(Path.Combine(workspace.Root, ".manager-ui-fixture")) || fixtureIds.Length != 2 || profiles.Count != 2
                || profiles.Any(p => !fixtureIds.Contains(p.Id) || p.ExistingDirectory != null)) throw new IOException("Runtime UI tests require exactly the two freshly imported fixture servers");
            foreach (var p in profiles) {
                string assembly = Path.Combine(workspace.ServerDirectory(p), "Manager.ConsoleProbe.dll");
                if (!File.Exists(assembly) || System.Reflection.AssemblyName.GetAssemblyName(assembly).Name != "Manager.ConsoleProbe") throw new IOException("Runtime UI fixture identity mismatch");
            }
        }
        string FixturePath(string option) {
            int index = Array.IndexOf(Program.Args, option);
            if (index < 0 || Program.Args.Length <= index + 1) throw new IOException("Missing test artifact: " + option);
            return Path.GetFullPath(Program.Args[index + 1]);
        }
        async Task Wait(Func<bool> ready, int milliseconds = 30_000) {
            var watch = Stopwatch.StartNew();
            while (!ready() && watch.ElapsedMilliseconds < milliseconds) await Task.Delay(60);
            if (!ready()) throw new IOException("Runtime UI fixture wait timed out");
        }
        async Task<Reply> Request(Request request) {
            var reply = await LibraryRequest(request, 120_000);
            return reply;
        }
        async Task WaitQueues() {
            await Wait(() => startupQueue?.Active != true && stopQueue?.Active != true && !busy, 120_000);
            await RefreshState();
        }
        async Task SelectTarget(string id) {
            PluginTarget.SelectedItem = PluginTarget.Items.Cast<ComboBoxItem>().Single(i => (string?)i.Tag == id);
            await Wait(() => runtimeLoaded && !runtimeLoading && !libraryLoading && runtimeTargetId == id);
        }
        async Task Choose(PluginArtifact artifact, bool selected) {
            var row = artifactChoices.Single(r => ArtifactKey(r.Artifact) == ArtifactKey(artifact));
            ArtifactList.ScrollIntoView(row);
            CheckBox? VisibleChoice() => ArtifactList.GetVisualDescendants().OfType<CheckBox>()
                .SingleOrDefault(c => ReferenceEquals(c.DataContext, row) && c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0);
            await Wait(() => VisibleChoice() != null, 5000);
            // Operate the rendered checkbox and its real TwoWay binding.
            var box = VisibleChoice()!;
            if (!box.IsEffectivelyEnabled) throw new IOException("Runtime UI artifact checkbox is disabled");
            box.IsChecked = selected;
            await Wait(() => box.IsChecked == selected && row.Selected == selected, 3000);
        }
        async Task Save() {
            if (!SaveRuntimeButton.IsEnabled) throw new IOException("Runtime UI save button is disabled");
            SaveRuntimeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !libraryBusy);
        }
        async Task ConfirmImport() {
            ImportCurrentPluginsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Window? dialog() => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
                .FirstOrDefault(w => w.Owner == this && w.Title == "收录原服插件");
            await Wait(() => dialog() != null, 3000);
            dialog()!.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "确认")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => libraryBusy || PluginStatus.Text?.Contains("已收录") == true, 3000);
            await Wait(() => !libraryBusy);
        }
        RequireFixtures();
        string v1 = FixturePath("--runtime-plugin-v1"), v2 = FixturePath("--runtime-plugin-v2");
        var original = new Dictionary<string, RuntimeSelection>();
        var initiallyRunning = states.Where(s => fixtureIds.Contains(s.Profile.Id) && s.Identity != null).Select(s => s.Profile.Id).ToHashSet();
        string? originalTarget = PluginTargetId;
        var evidence = new List<object>();
        var parkedFixtureFiles = new List<(string Source, string Parked)>();
        try {
            await Wait(() => !libraryLoading && !runtimeLoading);
            Check("running-target-is-readonly", !SaveRuntimeButton.IsEnabled && !RuntimeEnabled.IsEnabled && !EngineVersion.IsEnabled
                && !ImportCurrentPluginsButton.IsEnabled && !ImportCurrentEngineButton.IsEnabled);
            Check("legacy-copy-mode-starts-folded", !LegacyLibraryExpander.IsExpanded);
            foreach (string id in fixtureIds) original[id] = (await Request(new("runtime-selection", id))).RuntimeSelection!;
            await Request(new("stop-all")); await RefreshState();
            await Wait(() => stopQueue?.Active != true && states.All(s => s.Identity == null), 120_000);
            RequireFixtures();
            var catalog = await Request(new("shared-library-import", Files: [v1, v2]));
            var a1 = catalog.Artifacts!.Single(p => p.Sha256 == Hashes.FileSha(v1));
            var a2 = catalog.Artifacts!.Single(p => p.Sha256 == Hashes.FileSha(v2));
            Check("both-same-name-versions-preserved", a1.Name == a2.Name && a1.Sha256 != a2.Sha256 && a1.Version != a2.Version);
            await Request(new("runtime-selection-save", fixtureIds[1], Json.Encode(new RuntimeSelection(false, [a1]))));
            RefreshLibraryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !libraryLoading && !runtimeLoading && libraryArtifacts.Length >= 2);
            await SelectTarget(fixtureIds[0]);
            Check("stopped-target-becomes-editable", SaveRuntimeButton.IsEnabled && RuntimeEnabled.IsEnabled && EngineVersion.IsEnabled);
            Check("empty-initial-selection", runtimePlugins.Count == 0 && RuntimeEnabled.IsChecked == false);
            string plugins = Path.Combine(workspace.ServerDirectory(states.Single(s => s.Profile.Id == fixtureIds[0]).Profile), "plugins");
            await Task.Run(() => { Directory.CreateDirectory(plugins); File.Copy(v1, Path.Combine(plugins, Path.GetFileName(v1)), false); });
            await ConfirmImport();
            var imported = (await Request(new("runtime-selection", fixtureIds[0]))).RuntimeSelection!;
            Check("import-current-button-locks-version-without-enabling", !imported.Enabled && imported.Plugins.Any(p => p.Sha256 == a1.Sha256) && RuntimeEnabled.IsChecked == false);
            await Choose(a1, true); await Choose(a2, true);
            Check("checkbox-binding-enforces-one-version", runtimePlugins.Count == 1 && runtimePlugins.Values.Single().Sha256 == a2.Sha256
                && artifactChoices.Single(r => r.Artifact.Sha256 == a1.Sha256).Selected == false);
            var items = ArtifactList.ItemsSource;
            await Task.Delay(1800);
            Check("poll-does-not-rebuild-or-overwrite-draft", runtimeDirty && ReferenceEquals(items, ArtifactList.ItemsSource) && runtimePlugins.Values.Single().Sha256 == a2.Sha256);
            await Save();
            var saved = (await Request(new("runtime-selection", fixtureIds[0]))).RuntimeSelection!;
            Check("disabled-selection-saves-exact-hash", !saved.Enabled && saved.Plugins.Single().Sha256 == a2.Sha256);
            await SelectTarget(fixtureIds[1]);
            Check("switch-loads-second-server-own-choice", runtimePlugins.Count == 1 && runtimePlugins.Values.Single().Sha256 == a1.Sha256);
            await SelectTarget(fixtureIds[0]);
            Check("switch-back-preserves-first-server-choice", runtimePlugins.Count == 1 && runtimePlugins.Values.Single().Sha256 == a2.Sha256);
            await Choose(a2, false); RuntimeEnabled.IsChecked = false; await Save();
            var empty = (await Request(new("runtime-selection", fixtureIds[0]))).RuntimeSelection!;
            Check("disabled-empty-selection-saves", !empty.Enabled && empty.Plugins.Length == 0);
            Check("empty-save-does-not-change-other-server", (await Request(new("runtime-selection", fixtureIds[1]))).RuntimeSelection!.Plugins.Single().Sha256 == a1.Sha256);
            await Choose(a2, true); RuntimeEnabled.IsChecked = true;
            Check("unsaved-preview-blocked", runtimeDirty && !PreviewRuntimeButton.IsEnabled);
            ImportCurrentEngineButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !libraryBusy);
            Check("core-import-populates-real-combobox", libraryEngines.Length > 0 && EngineVersion.Items.Count > 1);
            Check("core-import-does-not-switch-automatically", runtimeEngineRevision == null);
            var engineItem = EngineVersion.Items.Cast<ComboBoxItem>().First(i => i.Tag is string);
            EngineVersion.SelectedItem = engineItem;
            RuntimeEnabled.IsChecked = false;
            await Save();
            var preselected = (await Request(new("runtime-selection", fixtureIds[0]))).RuntimeSelection!;
            Check("disabled-sandbox-saves-core-preselection", !preselected.Enabled && preselected.EngineRevision == (string)engineItem.Tag!
                && preselected.Plugins.Single().Sha256 == a2.Sha256);
            RuntimeEnabled.IsChecked = true;
            await Save();
            saved = (await Request(new("runtime-selection", fixtureIds[0]))).RuntimeSelection!;
            Check("core-combobox-saves-exact-revision", saved.Enabled && saved.EngineRevision == (string)engineItem.Tag! && saved.Plugins.Single().Sha256 == a2.Sha256);
            await SelectTarget(fixtureIds[1]); await SelectTarget(fixtureIds[0]);
            Check("core-revision-restores-on-target-switch", (EngineVersion.SelectedItem as ComboBoxItem)?.Tag as string == saved.EngineRevision);
            // Probe writes test controls/events at its root. Preserve them outside the
            // source during preview, which correctly rejects unknown mutable root files.
            RequireFixtures();
            if ((await Request(new("list"))).Servers!.Any(s => s.Identity != null)) throw new IOException("Probe must remain stopped before parking its test files");
            string source = workspace.ServerDirectory(states.Single(s => s.Profile.Id == fixtureIds[0]).Profile);
            await Task.Run(() => {
                string parked = Path.Combine(output, "probe-root-state", fixtureIds[0]);
                Directory.CreateDirectory(parked);
                foreach (string name in new[] { "probe-delay-ms.txt", "probe-off-delay-ms.txt", "probe-events.jsonl" }) {
                    string from = Path.Combine(source, name), to = Path.Combine(parked, name);
                    if (!File.Exists(from)) continue;
                    File.Move(from, to);
                    parkedFixtureFiles.Add((from, to));
                }
            });
            PreviewRuntimeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !libraryBusy);
            Check("preview-button-shows-actual-existing-path", RuntimePreviewPath.IsVisible && Directory.Exists(RuntimePreviewPath.Text));
            var previewState = await Request(new("list"));
            Check("preview-does-not-launch-server-or-queue", previewState.Servers!.All(s => s.Identity == null) && previewState.StartupQueue?.Active != true && previewState.StopQueue?.Active != true);
            evidence.Add(new { id = fixtureIds[0], selection = saved, preview = RuntimePreviewPath.Text, libraryArtifacts, libraryEngines });
            await snap("runtime-01-shared-library-and-core");
            Width = 1220; Height = 800; await snap("runtime-02-minimum-library"); Width = 1510; Height = 1000;
            LibrarySearch.Text = a2.Sha256[..16]; await Task.Delay(100);
            Check("search-filters-full-hash", ArtifactList.Items.Count == 1); LibrarySearch.Text = ""; await Task.Delay(100);
            LegacyLibraryExpander.IsExpanded = true; await Task.Delay(150); await snap("runtime-03-legacy-mode-separated"); LegacyLibraryExpander.IsExpanded = false;
            Check("shared-library-retains-older-version", (await Request(new("shared-library-list"))).Artifacts!.Any(p => p.Sha256 == a1.Sha256));
        } finally {
            RequireFixtures();
            foreach (var pair in original) await Request(new("runtime-selection-save", pair.Key, Json.Encode(pair.Value)));
            await Task.Run(() => {
                foreach (var pair in parkedFixtureFiles) {
                    File.Copy(pair.Parked, pair.Source, overwrite: false);
                    if (Hashes.FileSha(pair.Source) != Hashes.FileSha(pair.Parked)) throw new IOException("Probe test file restore SHA mismatch");
                }
            });
            foreach (string id in initiallyRunning) {
                await Request(new("start", id)); await RefreshState();
                await WaitQueues();
                await Wait(() => states.Single(s => s.Profile.Id == id).State == "RUNNING", 120_000);
            }
            if (originalTarget != null) await SelectTarget(originalTarget);
            await Task.Run(() => Json.Save(Path.Combine(output, "runtime-library-ui.json"), new {
                evidence, restored = true, fixtureIds, parkedFixtureFiles = parkedFixtureFiles.Select(p => new { p.Source, p.Parked }),
                scope = "Actual Avalonia controls and live local Host IPC; only the two freshly imported Probe fixtures", productionTouched = false
            }));
        }
    }
}
