using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

Console.InputEncoding = Console.OutputEncoding = Encoding.UTF8;
string root = Directory.GetCurrentDirectory();
string evidence = Environment.GetEnvironmentVariable("RUNTIME_FIXTURE_EVENTS") ?? Path.Combine(root, "fixture-events.jsonl");
using var eventMutex = new Mutex(false, "Local\\RuntimeFixtureEvents_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence.ToUpperInvariant()))));
void Record(object value) {
    eventMutex.WaitOne();
    try {
        using var stream = new FileStream(evidence, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + Environment.NewLine);
        stream.Write(bytes); stream.Flush(true);
    } finally { eventMutex.ReleaseMutex(); }
}
using var launcher = JsonDocument.Parse(File.ReadAllText("config/config.json"));
string server = launcher.RootElement.GetProperty("fixtureServer").GetString()!;
string[] worlds = launcher.RootElement.GetProperty("launcher").GetProperty("autoStartServers").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray();
using var shock = JsonDocument.Parse(File.ReadAllText("config/TShockAPI/config.json"));
int port = shock.RootElement.GetProperty("RestApiPort").GetInt32();
var loaded = new List<object>();
string? core = null;
if (File.Exists("Fixture.Core.dll")) {
    var context = new AssemblyLoadContext("fixture-core", isCollectible: true);
    try {
        var assembly = context.LoadFromAssemblyPath(Path.Combine(root, "Fixture.Core.dll"));
        core = (string)assembly.GetType("RuntimeFixture.Plugin.Entry")!.GetMethod("Identity")!.Invoke(null, null)!;
    } finally { context.Unload(); }
}
foreach (string folder in new[] { "plugins", "ServerPlugins" }.Where(Directory.Exists)) {
    foreach (string path in Directory.GetFiles(folder, "*.dll").OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) {
        var context = new AssemblyLoadContext("fixture-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try {
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
            var entry = assembly.GetType("RuntimeFixture.Plugin.Entry");
            if (entry == null) continue;
            string result = (string)entry.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { root, server })!;
            loaded.Add(new { file = Path.GetFileName(path), version = assembly.GetName().Version!.ToString(), result,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), location = assembly.Location, alc = context.Name });
        } catch (Exception e) {
            Record(new { action = "plugin-error", server, file = path, error = e.ToString() }); return 23;
        } finally { context.Unload(); }
    }
}
foreach (string folder in new[] { "config", "Data", "world" }) {
    Directory.CreateDirectory(folder);
    string path = Path.Combine(folder, "fixture-" + server + (folder == "world" ? ".wld" : ".txt"));
    int previous = File.Exists(path) ? int.Parse(File.ReadAllText(path)) : 0;
    File.WriteAllText(path, (previous + 1).ToString());
}
Record(new { action = "start", server, pid = Environment.ProcessId, cwd = root, executable = Environment.ProcessPath, core, loaded });
using var quit = new CancellationTokenSource();
using var listener = new HttpListener();
listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
listener.Start();
var rest = Task.Run(async () => {
    try {
        while (!quit.IsCancellationRequested) {
            var context = await listener.GetContextAsync().WaitAsync(quit.Token);
            string path = context.Request.Url!.AbsolutePath;
            string? name = context.Request.QueryString["server"];
            object body;
            if (path == "/world/list") body = new { status = "200", worlds = worlds.Select(n => new { name = n, running = true, width = 8400, height = 2400 }) };
            else if (path == "/v2/server/status" && worlds.Contains(name)) body = new { status = "200", name };
            else if (path == "/v2/world/save" && worlds.Contains(name)) {
                Record(new { action = "save", server, world = name, pid = Environment.ProcessId });
                body = new { status = "200", response = "World saved" };
            } else body = new { status = "404" };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    } catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
});
Console.WriteLine("RUNTIME_FIXTURE_READY " + server);
while (Console.ReadLine() is { } line) {
    string command = line.Trim();
    Record(new { action = "command", server, command, pid = Environment.ProcessId });
    if (command == "/off") break;
    Console.WriteLine("RUNTIME_ECHO " + server + " " + command);
}
Record(new { action = "stop", server, pid = Environment.ProcessId });
quit.Cancel(); listener.Stop(); await rest;
return 0;
