using System.Net.Http;
using System.Text.Json;

namespace Pigeon.Manager.Host;

// HTTP 200 alone also occurs for missing/stopped worlds. Check the API body and
// every configured name, then query each world's status before releasing the queue.
sealed class WorldReadiness : IDisposable {
    readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly string[] names;
    readonly int port;
    readonly string token;
    public WorldReadiness(string serverRoot) {
        using var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(serverRoot, "config", "config.json")));
        var launcher = root.RootElement.GetProperty("launcher");
        names = launcher.GetProperty("autoStartServers").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString() ?? "").ToArray();
        if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new IOException("未配置有效且唯一的 autoStartServers，不能确认世界就绪");
        using var shock = JsonDocument.Parse(File.ReadAllText(Path.Combine(serverRoot, "config", "TShockAPI", "config.json")));
        var config = shock.RootElement;
        if (config.TryGetProperty("Settings", out var settings)) config = settings;
        port = config.TryGetProperty("RestApiPort", out var enPort) ? enPort.GetInt32()
            : config.GetProperty("Rest的端口").GetInt32();
        var tokens = config.TryGetProperty("ApplicationRestTokens", out var enTokens) ? enTokens
            : config.GetProperty("Rest外部应用令牌字典");
        token = tokens.EnumerateObject().FirstOrDefault().Name ?? "";
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(token)) throw new IOException("缺少世界就绪核验的本地 REST 端口或令牌");
    }
    async Task<JsonDocument> Get(string path, CancellationToken cancel, Func<bool>? isCurrent = null, int timeoutSeconds = 3) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        RequireCurrent(isCurrent);
        var separator = path.Contains('?') ? '&' : '?';
        using var reply = await http.GetAsync($"http://127.0.0.1:{port}/{path}{separator}token={Uri.EscapeDataString(token)}", deadline.Token);
        RequireCurrent(isCurrent);
        reply.EnsureSuccessStatusCode();
        if (reply.StatusCode != System.Net.HttpStatusCode.OK) throw new IOException("REST HTTP status 必须为 200");
        string body = await reply.Content.ReadAsStringAsync(deadline.Token);
        RequireCurrent(isCurrent);
        return JsonDocument.Parse(body);
    }
    static void RequireCurrent(Func<bool>? isCurrent) {
        if (isCurrent != null && !isCurrent()) throw new IOException("STOP_IDENTITY_CHANGED：所属终端或进程身份已变化");
    }
    static bool Success(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("status", out var status) && status.ToString() == "200";
    // Only this exact native REST response confirms a save. A stopped world is
    // recorded separately and never counted as a successfully saved world.
    public async Task<string> SaveRunning(Func<bool> isCurrent, Action<string, string> report, CancellationToken cancel) {
        using var list = await Get("world/list", cancel, isCurrent);
        if (!Success(list.RootElement)) throw new IOException("世界列表 status 不是 200；未确认保存");
        var worlds = list.RootElement.GetProperty("worlds").EnumerateArray().ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var world in worlds) {
            string name = world.GetProperty("name").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)
                || world.GetProperty("running").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new IOException("世界列表名称或运行状态无效；未确认保存");
        }
        int saved = 0, stopped = 0;
        foreach (var world in worlds) {
            RequireCurrent(isCurrent);
            string name = world.GetProperty("name").GetString()!;
            if (!world.GetProperty("running").GetBoolean()) { stopped++; report(name, "NO_RUNNING_WORLD"); continue; }
            using var result = await Get("v2/world/save?server=" + Uri.EscapeDataString(name), cancel, isCurrent, 30);
            if (!Success(result.RootElement) || !result.RootElement.TryGetProperty("response", out var response)
                || response.ValueKind != JsonValueKind.String || response.GetString() != "World saved") {
                report(name, "SAVE_FAILED");
                throw new IOException("SAVE_FAILED：世界 " + name + " 未返回 status=200 且 response=World saved");
            }
            RequireCurrent(isCurrent);
            saved++; report(name, "SAVED");
        }
        RequireCurrent(isCurrent);
        if (worlds.Length == 0) report("", "NO_RUNNING_WORLD");
        return saved == 0 ? "NO_RUNNING_WORLD" : $"SAVED={saved}; NO_RUNNING_WORLD={stopped}";
    }
    public async Task<(bool Ready, string Detail)> Check(CancellationToken cancel) {
        try {
            using var list = await Get("world/list", cancel);
            if (!Success(list.RootElement)) return (false, "世界列表尚未就绪");
            var worlds = list.RootElement.GetProperty("worlds").EnumerateArray().ToArray();
            foreach (string name in names) {
                var matches = worlds.Where(x => x.GetProperty("name").GetString() == name).ToArray();
                if (matches.Length != 1 || !matches[0].GetProperty("running").GetBoolean()
                    || matches[0].GetProperty("width").GetInt32() <= 0 || matches[0].GetProperty("height").GetInt32() <= 0)
                    return (false, "等待世界：" + name);
                using var status = await Get("v2/server/status?server=" + Uri.EscapeDataString(name), cancel);
                if (!Success(status.RootElement) || status.RootElement.GetProperty("name").GetString() != name)
                    return (false, "等待世界状态：" + name);
            }
            return (true, $"全部 {names.Length} 个世界已就绪");
        } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) {
            cancel.ThrowIfCancellationRequested();
            // Never persist URLs or exception messages containing the REST token.
            return (false, "等待本地 REST 与全部世界就绪");
        }
    }
    public void Dispose() => http.Dispose();
}
