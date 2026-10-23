using System.Diagnostics;

namespace Pigeon.Manager.Desktop;

// One background reader, one replaceable result: a slow UI cannot accumulate
// dispatcher callbacks or a second backlog of logs. Only visible panes are read.
sealed class ConsoleCollector : IDisposable {
    public sealed record Selection(long Version, string? Id, string Name, ProcessIdentity? Identity, bool Owned, bool Visible, int Active, bool Triple);
    public sealed record Pane(string Title, string? World, bool IsHost, TerminalSession? Model, string Hint);
    public sealed record Batch(long Version, WorldConsoleInfo[] Worlds, Pane[] Panes);
    readonly HostClient client;
    readonly CancellationTokenSource quit = new();
    readonly Dictionary<string, TerminalSession> models = [];
    Selection selection = new(0, null, "", null, false, false, 0, false);
    Batch? latest;
    public Batch? Latest => Volatile.Read(ref latest);
    public long Requests => Interlocked.Read(ref requests);
    long requests;
    public ConsoleCollector(HostClient client) { this.client = client; _ = Task.Run(Run); }
    public void Select(Selection value) => Volatile.Write(ref selection, value);
    async Task<Reply> Send(Request request) { Interlocked.Increment(ref requests); return await client.Send(request, 4000).ConfigureAwait(false); }
    async Task Run() {
        WorldConsoleInfo[] worlds = [];
        string? owner = null;
        var worldCheck = Stopwatch.StartNew();
        while (!quit.IsCancellationRequested) {
            var selected = Volatile.Read(ref selection);
            try {
                if (selected.Visible && selected.Id != null) {
                    string key = selected.Id + ":" + selected.Identity?.StartedUtcTicks;
                    if (key != owner) { worlds = []; owner = key; worldCheck.Restart(); }
                    if (selected.Owned && (worlds.Length == 0 || worldCheck.ElapsedMilliseconds >= 1500)) {
                        var list = await Send(new("world-consoles", selected.Id)).ConfigureAwait(false);
                        worlds = list.Ok && list.Worlds != null ? list.Worlds.OrderBy(w => w.Name.EndsWith("·西", StringComparison.Ordinal) ? 0 : w.Name.EndsWith("·东", StringComparison.Ordinal) ? 2 : 1).ThenBy(w => w.Index).Take(3).ToArray() : [];
                        worldCheck.Restart();
                    }
                    if (!selected.Owned) worlds = [];
                    bool host = selected.Active < 0 || worlds.Length == 0;
                    var panes = new List<Pane>();
                    foreach (int slot in host ? new[] { -1 } : selected.Triple ? new[] { 0, 1, 2 } : new[] { selected.Active }) {
                        if (slot >= worlds.Length) { panes.Add(new($"{slot+1} · 等待世界", null, false, null, "世界尚未接入")); continue; }
                        string? name = slot < 0 ? null : worlds[slot].Name;
                        string modelKey = key + "|" + (name ?? "host");
                        if (!models.TryGetValue(modelKey, out var model)) {
                            // At most the current and a few recently selected consoles.
                            if (models.Count >= 12) models.Clear();
                            model = new();
                            if (name == null) {
                                string id = selected.Id;
                                var identity = selected.Identity;
                                model.Response = text => { if (identity != null) _ = Respond(id, identity, text); };
                            }
                            models[modelKey] = model;
                        }
                        var log = await Send(new(name == null ? "read" : "world-consoles", selected.Id, Cursor: model.Cursor, World: name)).ConfigureAwait(false);
                        if (quit.IsCancellationRequested) return;
                        model.Apply(log.Ok ? log.Log : null);
                        panes.Add(new(name == null ? "宿主控制台 · " + selected.Name : $"{slot+1} · {name}", name, name == null, model,
                            selected.Identity == null ? "服务器已停止" : name == null ? "等待世界控制台接入；当前显示宿主输出" : worlds[slot].Running ? "等待世界输出" : "世界加载中"));
                    }
                    Volatile.Write(ref latest, new(selected.Version, worlds, panes.ToArray()));
                }
                await Task.Delay(selected.Visible ? 250 : 750, quit.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (quit.IsCancellationRequested) { break; }
            catch {
                // IPC timeouts do not block lifecycle requests or post errors every tick.
                try { await Task.Delay(750, quit.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }
    async Task Respond(string id, ProcessIdentity identity, string text) { try { await client.Send(new("send", id, text, Identity: identity)).ConfigureAwait(false); } catch { } }
    public void Dispose() => quit.Cancel();
}
