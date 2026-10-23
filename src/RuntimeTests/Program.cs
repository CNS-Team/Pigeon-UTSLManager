using Pigeon.Manager;
using RuntimeChecks;

if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Runtime fixtures require Windows file IDs and the real ConPTY Host."); return 2; }
if (args.Length != 5 && args.Length < 9) {
    Console.Error.WriteLine("Usage: Manager.RuntimeTests <new-evidence-directory> <Host.exe> <fixture-package> <plugin-v1.dll> <plugin-v2.dll>");
    return 2;
}
string output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Evidence directory exists; refusing to overwrite");
Directory.CreateDirectory(output);
var evidence = new Evidence(output);
string host = Path.GetFullPath(args[1]), package = Path.GetFullPath(args[2]);
string v1 = Path.GetFullPath(args[3]), v2 = Path.GetFullPath(args[4]);
string hostCore = Path.Combine(Path.GetDirectoryName(host)!, "Pigeon.UTSLManager.Core.dll");
string hostAssembly = Path.ChangeExtension(host, ".dll");
evidence.Require("fixture/v1-v2-same-name-different-hash",
    System.Reflection.AssemblyName.GetAssemblyName(v1).Name == System.Reflection.AssemblyName.GetAssemblyName(v2).Name && Hashes.FileSha(v1) != Hashes.FileSha(v2));
string scope = Environment.GetEnvironmentVariable("RUNTIME_TEST_SCOPE") ?? "all";
if (scope is not ("all" or "runtime" or "boundary" or "host" or "module")) throw new ArgumentException("Unknown RUNTIME_TEST_SCOPE: " + scope);
evidence.Write("inputs.json", new {
    scope, host, hostSha256 = Hashes.FileSha(host), hostAssembly, hostAssemblySha256 = Hashes.FileSha(hostAssembly),
    hostCore, hostCoreSha256 = Hashes.FileSha(hostCore), package,
    managerCore = typeof(Workspace).Assembly.Location, managerCoreSha256 = Hashes.FileSha(typeof(Workspace).Assembly.Location),
    v1, v1Sha256 = Hashes.FileSha(v1), v2, v2Sha256 = Hashes.FileSha(v2),
    actualCore = args.Length >= 9 ? Path.GetFullPath(args[5]) : null,
    providerSources = args.Skip(9).Select(Path.GetFullPath).ToArray()
});
if (scope is "all" or "host") evidence.Require("binding/Host-and-runner-use-identical-Core", Hashes.FileSha(hostCore) == Hashes.FileSha(typeof(Workspace).Assembly.Location));
if (scope is "all" or "runtime") await RuntimeScenarios.Run(evidence, output, package, v1, v2);
if (scope is "all" or "runtime") await LayoutScenarios.Run(evidence, output, package, v1, v2);
if (scope is "all" or "boundary") await BoundaryScenarios.Run(evidence, output, package, v1);
if (scope is "all" or "host") await HostScenarios.Run(evidence, output, host, package, v1, v2);
if ((scope is "all" or "module") && args.Length >= 9) {
    await ModuleScenarios.Run(evidence, output, package, v1, Path.GetFullPath(args[5]), Path.GetFullPath(args[6]), Path.GetFullPath(args[7]), Path.GetFullPath(args[8]));
    foreach (string source in args.Skip(9)) await ProviderScenarios.Run(evidence, output, package, Path.GetFullPath(args[6]), Path.GetFullPath(source));
}
evidence.Check("cleanup/no-fixture-processes-remain", Helpers.ProcessesUnder(output).Length == 0, Helpers.ProcessesUnder(output));
evidence.Save();
Console.WriteLine($"RESULT {evidence.Passed} passed, {evidence.Failed} failed; {Path.Combine(output, "result.json")}");
return evidence.Failed == 0 ? 0 : 1;
