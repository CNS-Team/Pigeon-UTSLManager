using UnifierTSL.Module;
[assembly: CoreModule]
namespace RuntimeFixture.CoreModule;
public static class Entry {
    static int calls;
    public static int Tick() => ++calls;
    public static string ExecuteSupport() => ModuleSupport.Entry.Execute();
}
