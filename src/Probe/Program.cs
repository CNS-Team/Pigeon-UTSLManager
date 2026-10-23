using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Net;
Console.InputEncoding = Console.OutputEncoding = Encoding.UTF8;
if (File.Exists("probe-exit-before-ready.txt")) _ = Task.Run(async () => { await Task.Delay(250); Environment.Exit(9); });
using var quit = new CancellationTokenSource();
using var listener = new HttpListener();
var fixtureLogSync = new object();
void Record(object value) { lock (fixtureLogSync) File.AppendAllText("probe-events.jsonl", JsonSerializer.Serialize(value) + Environment.NewLine); }
// Local test fixture only; this executable is never used in a production server.
if (File.Exists("config/TShockAPI/config.json")) {
    using var cfg = JsonDocument.Parse(File.ReadAllText("config/TShockAPI/config.json"));
    int port = cfg.RootElement.GetProperty("RestApiPort").GetInt32();
    using var launcher = JsonDocument.Parse(File.ReadAllText("config/config.json"));
    var names = launcher.RootElement.GetProperty("launcher").GetProperty("autoStartServers").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray();
    int delay = File.Exists("probe-delay-ms.txt") ? int.Parse(File.ReadAllText("probe-delay-ms.txt")) : 0;
    bool missingStatus = File.Exists("probe-missing-status.txt");
    var elapsed = Stopwatch.StartNew();
    WorldConsoleProbe.Start(names, quit.Token);
    listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
    _ = Task.Run(async () => {
        try {
            while (!quit.IsCancellationRequested) {
                var ctx = await listener.GetContextAsync().WaitAsync(quit.Token);
                // One hanging save must not block unrelated REST requests in this fixture.
                _ = Task.Run(async () => {
                try {
                string? name = ctx.Request.QueryString["server"];
                bool ready = elapsed.ElapsedMilliseconds >= delay;
                string path = ctx.Request.Url!.AbsolutePath;
                bool running = ready && !File.Exists("probe-worlds-stopped.txt");
                object body;
                if (path == "/v2/world/save") {
                    Record(new { action = "save", name, pid = Environment.ProcessId });
                    if (File.Exists("probe-save-hang.txt")) await Task.Delay(Timeout.Infinite, quit.Token);
                    if (File.Exists("probe-save-delay-ms.txt")) await Task.Delay(int.Parse(File.ReadAllText("probe-save-delay-ms.txt")), quit.Token);
                    string mode = File.Exists("probe-save-mode.txt") ? File.ReadAllText("probe-save-mode.txt").Trim() : "ok";
                    body = !running || !names.Contains(name) ? new { status = "404", response = "Server was not found" }
                        : mode == "status-fail" ? new { status = "500", response = "World saved" }
                        : mode == "body-fail" ? new { status = "200", response = "World saved failed" }
                        : mode == "nested-fail" ? (object)new { status = "200", response = new { message = "World saved" } }
                        : mode == "numeric" ? (object)new { status = 200, response = "World saved" }
                        : new { status = "200", response = "World saved" };
                    if (mode == "http-fail") ctx.Response.StatusCode = 500;
                } else body = path == "/world/list"
                    ? new { status = "200", worlds = names.Select(n => new { name = n, running, width = 8400, height = 2400 }) }
                    : path == "/v2/server/status" && ready && !missingStatus && names.Contains(name) ? new { status = "200", name }
                    : new { status = "404", error = "Server was not found" };
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
                ctx.Response.ContentType = "application/json"; await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
                } catch (Exception e) when (e is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException) { ctx.Response.Abort(); }
                });
            }
        } catch (Exception) when (quit.IsCancellationRequested) { }
    });
}
int columns = 140, rows = 40;
try { if (!Console.IsOutputRedirected) { columns = Console.WindowWidth; rows = Console.WindowHeight; } } catch (IOException) { }
Console.WriteLine($"\u001b[36mCONPTY_READY PID={Environment.ProcessId} SIZE={columns}x{rows} 中文控制台\u001b[0m");
string? line;
while ((line = Console.ReadLine()) != null) {
    if (line.Trim() == "/off") {
        Record(new { action = "off", pid = Environment.ProcessId });
        if (File.Exists("probe-off-delay-ms.txt")) await Task.Delay(int.Parse(File.ReadAllText("probe-off-delay-ms.txt")), quit.Token);
        if (File.Exists("probe-off-hang.txt")) { Console.WriteLine("OFF_HANG"); await Task.Delay(Timeout.Infinite, quit.Token); }
        if (File.Exists("probe-off-residual.txt")) { Console.WriteLine("OFF_RESIDUAL"); continue; }
        Console.WriteLine("SAVED_AND_STOPPED"); break;
    }
    if (line.Trim() == "flood") {
        int count = File.Exists("probe-flood-count.txt") ? int.Parse(File.ReadAllText("probe-flood-count.txt")) : 10_000;
        count = Math.Clamp(count, 1, 1_000_000);
        Console.WriteLine("flood-start");
        for (int i = 0; i < count; i++) Console.WriteLine($"\u001b[36m洪流 {i:D6} 中文控制台\u001b[0m \u001b[32mANSI 后台解析性能测试\u001b[0m");
        Console.WriteLine("flood-end"); continue;
    }
    if (line.Trim() == "colors") Console.WriteLine("\u001b[31mRED\u001b[0m \u001b[32mGREEN\u001b[0m 中文回显");
    else Console.WriteLine("ECHO:" + line);
}
quit.Cancel();
