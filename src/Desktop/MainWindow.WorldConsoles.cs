using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
namespace Pigeon.Manager.Desktop;

public sealed partial class MainWindow {
    sealed class ConsolePane {
        public readonly Border Border = new() { Background = Brush.Parse("#111215"), BorderBrush = Brush.Parse("#3A3B43"), BorderThickness = new(1), CornerRadius = new(6), ClipToBounds = true };
        public readonly TerminalView Terminal = new();
        public readonly TextBox Input = new() { PlaceholderText = "控制台命令", FontFamily = FontFamily.Parse("Cascadia Mono,Microsoft YaHei UI") };
        public readonly Button Send = new() { Content = "发送", IsEnabled = false };
        public readonly TextBlock Title = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        public readonly TextBlock Hint = new() { Text = "等待控制台输出", Foreground = Brush.Parse("#8F93A3"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        public string? World;
        public bool IsHost;
    }
    readonly ConsolePane[] consolePanes = [new(), new(), new()];
    WorldConsoleInfo[] worlds = [];
    int activeWorld;
    int consoleGeneration;
    ConsoleCollector? consoleCollector;
    ConsoleCollector.Selection? consoleSelection;
    ConsoleCollector.Batch? appliedBatch;
    readonly Avalonia.Threading.DispatcherTimer consolePaint = new() { Interval = TimeSpan.FromMilliseconds(100) };
    void StartConsoleCollector() {
        consoleCollector = new(client);
        consolePaint.Tick += (_, _) => _ = RefreshWorldConsoles(); consolePaint.Start();
    }
    TerminalView PrimaryTerminal => consolePanes[0].Terminal;
    void CreateConsolePanes() {
        for (int i = 0; i < consolePanes.Length; i++) {
            var pane = consolePanes[i];
            var grid = new Grid { RowDefinitions = new("37,*,Auto") };
            var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(12,0) };
            header.Children.Add(pane.Title);
            var copy = new Button { Content = "复制", FontSize = 10, Padding = new(7,3), MinHeight = 24, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(copy, 1); header.Children.Add(copy);
            copy.Click += async (_, _) => { if (Clipboard != null && pane.Terminal.Session is { } model) await Clipboard.SetValueAsync(DataFormat.Text, model.PlainText()); };
            grid.Children.Add(header);
            var output = new Grid(); output.Children.Add(pane.Terminal); output.Children.Add(pane.Hint); Grid.SetRow(output, 1); grid.Children.Add(output);
            var input = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 7, Margin = new(9) };
            input.Children.Add(pane.Input); Grid.SetColumn(pane.Send, 1); input.Children.Add(pane.Send); Grid.SetRow(input, 2); grid.Children.Add(input);
            pane.Send.Click += async (_, _) => await SendPane(pane);
            pane.Input.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SendPane(pane); } };
            pane.Border.Child = grid; Consoles.Children.Add(pane.Border);
        }
        ApplyConsoleLayout();
    }
    void ClearConsolePanes() { consoleGeneration++; appliedBatch = null; foreach (var pane in consolePanes) { pane.Terminal.Session = null; pane.World = null; pane.IsHost = false; pane.Input.Text = ""; pane.Terminal.Refresh(); } ApplyConsoleLayout(); }
    void ApplyConsoleLayout() {
        if (Consoles == null) return;
        bool triple = TripleToggle.IsChecked == true && activeWorld >= 0;
        Consoles.ColumnDefinitions = new(triple ? "*,*,*" : "*");
        for (int i = 0; i < consolePanes.Length; i++) { var p = consolePanes[i]; p.Border.IsVisible = triple || i == 0; Grid.SetColumn(p.Border, triple ? i : 0); }
        var tabs = new[] { WorldTab1, WorldTab2, WorldTab3 };
        for (int i = 0; i < tabs.Length; i++) { tabs[i].IsEnabled = i < worlds.Length; tabs[i].Classes.Set("primary", activeWorld == i); ToolTip.SetTip(tabs[i], i < worlds.Length ? worlds[i].Name : "世界尚未登记"); }
    }
    async void WorldTabClick(object? sender, RoutedEventArgs e) {
        if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out int index) || index is < 0 or > 2) return;
        activeWorld = index; ClearConsolePanes(); await RefreshWorldConsoles();
    }
    async void WorldLayoutChanged(object? sender, RoutedEventArgs e) { if (workspace == null) return; if (TripleToggle.IsChecked == true && activeWorld < 0) activeWorld = 0; ClearConsolePanes(); await RefreshWorldConsoles(); }
    Task RefreshWorldConsoles() {
        // Publishing a selection is constant-time. No IPC, ANSI parsing, disk I/O
        // or waiting for a world runs on the dispatcher.
        var next = new ConsoleCollector.Selection(consoleGeneration, selectedId, Selected?.Profile.Name ?? "", Selected?.Identity,
            Selected?.Identity != null && Selected.State != "EXTERNAL", ServersPage.IsVisible && WindowState != WindowState.Minimized,
            activeWorld, TripleToggle.IsChecked == true);
        if (next != consoleSelection) { next = next with { Version = ++consoleGeneration }; consoleSelection = next; consoleCollector?.Select(next); }
        ApplyCollectedConsoles();
        return Task.CompletedTask;
    }
    void ApplyCollectedConsoles() {
        if (!ServersPage.IsVisible || WindowState == WindowState.Minimized) return;
        var batch = consoleCollector?.Latest;
        if (batch == null || batch.Version != consoleGeneration || ReferenceEquals(appliedBatch, batch)) return;
        appliedBatch = batch;
        string previous = string.Join('|', worlds.Select(w => w.Name)); worlds = batch.Worlds;
        if (previous != string.Join('|', worlds.Select(w => w.Name))) ApplyConsoleLayout();
        for (int i = 0; i < consolePanes.Length; i++) {
            var pane = consolePanes[i];
            var data = i < batch.Panes.Length ? batch.Panes[i] : null;
            pane.World = data?.World; pane.IsHost = data?.IsHost == true;
            pane.Title.Text = data?.Title ?? $"{i+1} · 等待世界";
            pane.Terminal.Session = data?.Model; pane.Terminal.Refresh();
            pane.Hint.Text = data?.Hint ?? "世界尚未接入"; pane.Hint.IsVisible = (data?.Model?.Cursor ?? 0) == 0;
        }
        UpdateSelected();
    }
    async Task SendPane(ConsolePane pane) {
        if (selectedId == null || string.IsNullOrWhiteSpace(pane.Input.Text) || (!pane.IsHost && pane.World is null)) return;
        try {
            var reply = pane.World is null
                ? await client.Send(new("send", selectedId, pane.Input.Text + "\r"))
                : await client.Send(new("world-send", selectedId, pane.Input.Text, World: pane.World), 4000);
            if (reply.Ok) pane.Input.Text = ""; else StatusText.Text = reply.Message;
        } catch (Exception e) { StatusText.Text = e.Message; }
    }
}
