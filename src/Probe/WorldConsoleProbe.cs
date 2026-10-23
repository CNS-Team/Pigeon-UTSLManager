using System.IO.Pipes;
using System.Text;
using System.Text.Json;

static class WorldConsoleProbe {
    sealed record Input(string Action, string? World, string? Value, long Cursor);
    sealed record Chunk(long Sequence, string Text);
    public static void Start(string[] names, CancellationToken cancel) {
        string? pipeName = Environment.GetEnvironmentVariable("UTSL_MANAGER_PIPE");
        if (pipeName == null) return;
        var output = names.ToDictionary(n => n, n => new List<Chunk> { new(1, $"\u001b[36m世界 {n} 中文控制台\u001b[0m\r\n") });
        _ = Task.Run(async () => {
            while (!cancel.IsCancellationRequested) {
                try {
                    using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancel);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    var request = JsonSerializer.Deserialize<Input>((await reader.ReadLineAsync(cancel))!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                    bool exists = request.World != null && output.ContainsKey(request.World);
                    object reply;
                    if (request.Action == "send") {
                        bool valid = exists && !string.IsNullOrWhiteSpace(request.Value) && request.Value.IndexOfAny(['\r', '\n', '\0']) < 0;
                        if (valid) { var log = output[request.World!]; log.Add(new(log.Count+1, "ECHO:" + request.Value + "\r\n")); }
                        reply = new { Ok = valid };
                    } else reply = new { Ok = request.World is null || exists, Worlds = names.Select((n, i) => new { Index = i+1, Name = n, Running = true }), Log = !exists ? null : new { Cursor = (long)output[request.World!].Count, Truncated = false, Chunks = output[request.World!].Where(c => c.Sequence > request.Cursor).ToArray() } };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(reply).AsMemory(), cancel);
                } catch (Exception) when (cancel.IsCancellationRequested) { break; }
            }
        });
    }
}
