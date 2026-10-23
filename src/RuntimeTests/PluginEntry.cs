using System.Text.Json;
namespace RuntimeFixture.Plugin;
public static class Entry {
    public static string Identity() {
#if FIXTURE_V2
        return "v2";
#else
        return "v1";
#endif
    }
    public static string Execute(string directory, string server) {
#if FIXTURE_V2
        const string version = "v2";
#else
        const string version = "v1";
#endif
        Directory.CreateDirectory(Path.Combine(directory, "Data"));
        string path = Path.Combine(directory, "Data", "plugin-" + server + ".json");
        int executions = File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("executions").GetInt32() : 0;
        File.WriteAllText(path, JsonSerializer.Serialize(new { executions = executions + 1, server, version, pid = Environment.ProcessId }));
        return version + ":" + server + ":" + (executions + 1);
    }
}
