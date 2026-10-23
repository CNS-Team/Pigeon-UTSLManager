using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
namespace Pigeon.Manager;
public sealed class HostClient(Workspace workspace, string hostExecutable) {
    public async Task<Reply> Send(Request request, int timeoutMs = 5000) {
        using var cancel = new CancellationTokenSource(timeoutMs);
        using var pipe = new NamedPipeClientStream(".", workspace.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(cancel.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        await writer.WriteLineAsync(Json.Encode(request).AsMemory(), cancel.Token);
        return Json.Decode<Reply>(await reader.ReadLineAsync(cancel.Token) ?? throw new IOException("后台连接已关闭"));
    }
    public async Task EnsureStarted() {
        try { await Send(new("ping"), 500); return; } catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException) { }
        if (!File.Exists(hostExecutable)) throw new FileNotFoundException("后台宿主缺失", hostExecutable);
        var psi = new ProcessStartInfo(hostExecutable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(hostExecutable)! };
        psi.ArgumentList.Add("--root"); psi.ArgumentList.Add(workspace.Root);
        Process.Start(psi)?.Dispose();
        for (int i = 0; i < 40; i++) { await Task.Delay(150); try { await Send(new("ping"), 400); return; } catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException) { } }
        throw new IOException("后台未能就绪，请检查 data/host-error.log");
    }
}
