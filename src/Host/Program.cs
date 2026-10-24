using System.IO.Pipes;
using System.Text;
using Pigeon.Manager;
using Pigeon.Manager.Host;

if (args.Length != 2 || args[0] != "--root") return;
var workspace = new Workspace(args[1]);
// A file lock covers interactive and SSH sessions alike; a Local mutex does not.
FileStream hostLock;
try { hostLock = new FileStream(Path.Combine(workspace.Data, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
catch (IOException) { return; }
using var singleInstance = hostLock;
try {
    var manager = new ServerManager(workspace);
    // Native reconciliation is restartable and retains every SHA-pinned revision.
    // A failed migration is recorded, without preventing existing servers from booting.
    try { var library = new SharedRuntime(workspace); if (library.ManagedLibraryEnabled) library.OrganizeLibrary(); }
    catch (Exception e) { workspace.Audit("plugin-library-reconcile-blocked", null, new { error = e.Message }); }
    workspace.Audit("host-start", null, new { pid = Environment.ProcessId, transport = "CurrentUserOnly named pipe" });
    // The queue runs independently, so IPC and consoles stay responsive during loading.
    var autostart = await manager.Handle(new("autostart"));
    workspace.Audit("host-autostart-result", null, new { autostart.Ok, autostart.Message });
    while (true) {
        var pipe = new NamedPipeServerStream(workspace.Pipe, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync();
        _ = Task.Run(async () => {
            using (pipe) using (var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true)) using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true }) {
                try {
                    using var deadline = new CancellationTokenSource(120_000);
                    string line = await reader.ReadLineAsync(deadline.Token) ?? "";
                    if (line.Length > 1_000_000) throw new IOException("请求超长");
                    var reply = await manager.Handle(Json.Decode<Request>(line));
                    await writer.WriteLineAsync(Json.Encode(reply));
                    if (reply.Message == "HOST_CAN_EXIT") Environment.Exit(0);
                } catch (Exception e) { try { await writer.WriteLineAsync(Json.Encode(new Reply(false, e.Message))); } catch { } }
            }
        });
    }
} catch (Exception e) { File.AppendAllText(Path.Combine(workspace.Data, "host-error.log"), DateTimeOffset.Now + " " + e + Environment.NewLine); }
