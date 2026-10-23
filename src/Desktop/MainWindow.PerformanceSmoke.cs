using System.Diagnostics;
using Avalonia.Threading;

namespace Pigeon.Manager.Desktop;

public sealed partial class MainWindow {
    async Task PerformanceSmoke(Action<string, bool> check, string output, string id) {
        int uiThread = Environment.CurrentManagedThreadId;
        var interval = Stopwatch.StartNew(); var delays = new List<double>();
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        heartbeat.Tick += (_, _) => { delays.Add(interval.Elapsed.TotalMilliseconds); interval.Restart(); };
        heartbeat.Start();
        var latency = Stopwatch.StartNew();
        await client.Send(new("send",id,"flood\r"));
        while (latency.ElapsedMilliseconds < 12000 && PrimaryTerminal.Session?.PlainText().Contains("flood-end") != true) await Task.Delay(50);
        heartbeat.Stop();
        var model = PrimaryTerminal.Session;
        check("flood-log-collector-roundtrip", model?.PlainText().Contains("flood-end") == true);
        check("vt-parser-never-on-ui-thread", model != null && model.ParserThreadId != uiThread && model.ParseCount > 0);
        check("ui-heartbeat-during-log-flood", delays.Count >= 5 && delays.Max() < 1000);
        long? parsed = model?.ParseCount; await Task.Delay(700);
        check("idle-output-not-reparsed", model != null && model.ParseCount == parsed);
        var sorted = delays.Order().ToArray();
        await Task.Run(() => Json.Save(Path.Combine(output,"console-performance.json"), new {
            at = DateTimeOffset.Now, uiThread, parserThread = model?.ParserThreadId,
            elapsedMs = latency.ElapsedMilliseconds, heartbeatSamples = sorted.Length,
            maxHeartbeatIntervalMs = sorted.LastOrDefault(), p95HeartbeatIntervalMs = sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length-1)*.95)],
            collectorRequests = consoleCollector?.Requests, parsedBatches = model?.ParseCount,
            architecture = "background IPC and VT parsing; immutable latest-only frames; dispatcher only draws changed visible panes",
            scope = "local actual Avalonia GUI with ANSI/Chinese ConPTY log flood; not a production load benchmark"
        }));
    }
}
