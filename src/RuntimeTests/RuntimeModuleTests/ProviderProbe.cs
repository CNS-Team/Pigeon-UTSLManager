using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

static class ProviderProbe {
    public static int Run(string coreDirectory, string modulesDirectory, string resultPath) {
        string core = Path.GetFullPath(coreDirectory), plugins = Path.GetFullPath(modulesDirectory), result = Path.GetFullPath(resultPath);
        if (File.Exists(result)) throw new IOException("Refuse to overwrite provider result");
        var checks = new List<object>(); int passed = 0, failed = 0;
        void Check(string name, bool pass, object? detail = null) {
            checks.Add(new { name, pass, detail }); if (pass) passed++; else failed++;
            Console.WriteLine((pass ? "PASS " : "FAIL ") + name);
        }
        string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        Dictionary<string, string> Snapshot() => Directory.GetFiles(plugins, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(plugins, path).Replace('\\', '/'), Sha, StringComparer.OrdinalIgnoreCase);
        var before = Snapshot();
        try {
            var resolver = new AssemblyDependencyResolver(Path.Combine(core, "UnifierTSL.dll"));
            AssemblyLoadContext.Default.Resolving += (_, name) => {
                string? path = resolver.ResolveAssemblyToPath(name);
                path ??= new[] { core, Path.Combine(core, "Core/bin"), Path.Combine(core, "Core/app"), Path.Combine(core, "Core/lib") }
                    .Select(directory => Path.Combine(directory, name.Name + ".dll")).FirstOrDefault(File.Exists);
                return path is null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            };
            var engine = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(core, "UnifierTSL.dll"));
            Check("provider/actual-core-loaded-from-generation", engine.Location == Path.Combine(core, "UnifierTSL.dll"),
                new { engine.Location, sha256 = Sha(engine.Location) });
            var type = engine.GetType("UnifierTSL.Module.ModuleAssemblyLoader", true)!;
            var mode = engine.GetType("UnifierTSL.Module.ModuleSearchMode", true)!;
            object loader = Activator.CreateInstance(type, plugins)!;
            var load = type.GetMethod("Load", [mode])!;
            object[] modules = ((IEnumerable)load.Invoke(loader, [Enum.Parse(mode, "NewOnly")])!).Cast<object>().ToArray();
            var assemblies = modules.Select(module => (Assembly)module.GetType().GetProperty("Assembly")!.GetValue(module)!).ToArray();
            string[] names = assemblies.Select(assembly => assembly.GetName().Name!).Order(StringComparer.Ordinal).ToArray();
            Check("provider/exact-real-modules-loaded", names.SequenceEqual(new[] { "CustomPlayer", "TShockAPI" }), names);
            foreach (var module in modules) {
                var assembly = (Assembly)module.GetType().GetProperty("Assembly")!.GetValue(module)!;
                string name = assembly.GetName().Name!;
                object attribute = assembly.GetCustomAttributes().Single(value => value.GetType().Name.StartsWith("ModuleDependenciesAttribute", StringComparison.Ordinal));
                object provider = attribute.GetType().GetProperty("DependenciesProvider")!.GetValue(attribute)!;
                var dependencies = ((IEnumerable)provider.GetType().GetMethod("GetDependencies")!.Invoke(provider, null)!).Cast<object>()
                    .Select(value => new { name = value.GetType().GetProperty("Name")!.GetValue(value)?.ToString(),
                        version = value.GetType().GetProperty("Version")!.GetValue(value)?.ToString() }).ToArray();
                int loaderDependencies = ((IEnumerable)module.GetType().GetProperty("Dependencies")!.GetValue(module)!).Cast<object>().Count();
                string[] expected = name == "TShockAPI" ? ["BCrypt.Net-Next", "GetText.NET", "HttpServer", "Microsoft.Data.Sqlite", "MySql.Data", "Npgsql", "linq2db"] : [];
                Check("provider/actual-execution-" + name, dependencies.Select(value => value.name).Order(StringComparer.Ordinal).SequenceEqual(expected)
                    && loaderDependencies == expected.Length,
                    new { provider = provider.GetType().FullName, dependencies, loaderDependencies });
            }
            object[] repeated = ((IEnumerable)load.Invoke(loader, [Enum.Parse(mode, "NewOnly")])!).Cast<object>().ToArray();
            Check("provider/no-duplicate-second-load", repeated.Length == 0);
        } catch (Exception error) { Check("provider/completed", false, error.ToString()); }
        var after = Snapshot();
        var changed = before.Where(pair => after.GetValueOrDefault(pair.Key) != pair.Value)
            .Select(pair => new { path = pair.Key, before = pair.Value, after = after.GetValueOrDefault(pair.Key) }).ToArray();
        string[] added = after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        Check("provider/only-private-config-may-change", changed.All(file => file.after is not null && file.path.EndsWith("/dependencies.json", StringComparison.OrdinalIgnoreCase))
            && added.Length == 0, new { changed, added });
        File.WriteAllText(result, JsonSerializer.Serialize(new { passed, failed, checks,
            scope = "Actual TShockAPI/CustomPlayer dependency providers through real ModuleAssemblyLoader; no game/server startup" },
            new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
