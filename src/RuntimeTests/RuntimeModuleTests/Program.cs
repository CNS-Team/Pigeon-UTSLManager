using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length == 4 && args[3] == "provider") return ProviderProbe.Run(args[0], args[1], args[2]);
if (args.Length != 3) { Console.Error.WriteLine("Usage: RuntimeModuleTests <actual-core-directory> <modules-directory> <new-result.json> [provider]"); return 2; }
string core = Path.GetFullPath(args[0]), plugins = Path.GetFullPath(args[1]), result = Path.GetFullPath(args[2]);
if (File.Exists(result)) throw new IOException("Refuse to overwrite module results");
var checks = new List<object>(); int passed = 0, failed = 0;
void Check(string name, bool pass, object? detail = null) {
    checks.Add(new { name, pass, detail }); if (pass) passed++; else failed++;
    Console.WriteLine((pass ? "PASS " : "FAIL ") + name);
}
string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
var before = Directory.GetFiles(plugins, "*.dll", SearchOption.AllDirectories).ToDictionary(path => Path.GetFileName(path), Sha);
try {
    var resolver = new AssemblyDependencyResolver(Path.Combine(core, "UnifierTSL.dll"));
    AssemblyLoadContext.Default.Resolving += (_, name) => {
        string? path = resolver.ResolveAssemblyToPath(name);
        path ??= new[] { core, Path.Combine(core, "Core/bin"), Path.Combine(core, "Core/app"), Path.Combine(core, "Core/lib") }
            .Select(directory => Path.Combine(directory, name.Name + ".dll")).FirstOrDefault(File.Exists);
        return path is null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    };
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(core, "UnifierTSL.dll"));
    Check("actual-core-loaded-from-requested-package", Path.GetFullPath(assembly.Location) == Path.Combine(core, "UnifierTSL.dll"),
        new { assembly.Location, bytes = new FileInfo(assembly.Location).Length, sha256 = Sha(assembly.Location) });
    var type = assembly.GetType("UnifierTSL.Module.ModuleAssemblyLoader", throwOnError: true)!;
    object loader = Activator.CreateInstance(type, plugins)!;
    var mode = assembly.GetType("UnifierTSL.Module.ModuleSearchMode")!;
    var load = type.GetMethod("Load", new[] { mode })!;
    object[] modules = ((IEnumerable)load.Invoke(loader, new[] { Enum.Parse(mode, "NewOnly") })!).Cast<object>().ToArray();
    var loaded = modules.Select(module => (Assembly)module.GetType().GetProperty("Assembly")!.GetValue(module)!).ToArray();
    Check("actual-loader-loaded-three-fixture-modules", loaded.Length == 3, loaded.Select(x => new { name = x.GetName().Name, context = AssemblyLoadContext.GetLoadContext(x)?.Name }).ToArray());
    var marker = loaded.Single(x => x.GetName().Name == "RuntimeFixture.Plugin");
    var markerResult = (string)marker.GetType("RuntimeFixture.Plugin.Entry")!.GetMethod("Execute")!.Invoke(null, new object[] { Directory.GetCurrentDirectory(), "actual-loader" })!;
    Check("root-no-attributes-marker-executed", markerResult == "v1:actual-loader:1", markerResult);
    string? coreModuleName = "RuntimeFixture.CoreModule", childModuleName = "RuntimeFixture.ChildModule";
    var coreModule = loaded.Single(x => x.GetName().Name == coreModuleName);
    var childModule = loaded.Single(x => x.GetName().Name == childModuleName);
    int coreCall = (int)coreModule.GetType(coreModuleName + ".Entry")!.GetMethod("Tick")!.Invoke(null, null)!;
    int childCall = (int)childModule.GetType(childModuleName + ".Entry")!.GetMethod("Tick")!.Invoke(null, null)!;
    Check("core-and-required-module-actually-share-state", coreCall == 1 && childCall == 2, new { coreCall, childCall });
    Check("core-and-required-module-share-ALC", ReferenceEquals(AssemblyLoadContext.GetLoadContext(coreModule), AssemblyLoadContext.GetLoadContext(childModule)));
    var supportResult = (string)coreModule.GetType(coreModuleName + ".Entry")!.GetMethod("ExecuteSupport")!.Invoke(null, null)!;
    var supportAssembly = AssemblyLoadContext.GetLoadContext(coreModule)!.Assemblies.Single(x => x.GetName().Name == "RuntimeFixture.ModuleSupport");
    Check("module-lib-dependency-actually-executed", supportResult == "side-dependency-executed", supportResult);
    Check("module-lib-dependency-loaded-in-core-ALC", ReferenceEquals(AssemblyLoadContext.GetLoadContext(coreModule), AssemblyLoadContext.GetLoadContext(supportAssembly)));
    Check("core-module-in-correct-subdirectory", File.Exists(Path.Combine(plugins, coreModuleName, coreModuleName + ".dll")));
    Check("required-core-module-in-core-subdirectory", File.Exists(Path.Combine(plugins, coreModuleName, childModuleName + ".dll")));
    Check("no-attributes-marker-at-root", File.Exists(Path.Combine(plugins, "RuntimeFixture.Plugin.dll")));
    var after = Directory.GetFiles(plugins, "*.dll", SearchOption.AllDirectories).ToDictionary(path => Path.GetFileName(path), Sha);
    Check("loader-preserves-all-selected-module-bytes", before.Count == after.Count && before.All(pair => after.GetValueOrDefault(pair.Key) == pair.Value));
    object[] repeated = ((IEnumerable)load.Invoke(loader, new[] { Enum.Parse(mode, "NewOnly") })!).Cast<object>().ToArray();
    Check("second-load-does-not-load-duplicates", repeated.Length == 0);
} catch (Exception ex) {
    Check("actual-loader-completed", false, ex.ToString());
}
File.WriteAllText(result, JsonSerializer.Serialize(new { passed, failed, checks,
    scope = "Real UnifierTSL ModuleAssemblyLoader + CoreModule/RequiresCoreModule placement and execution; no world boot or player E2E" },
    new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;
