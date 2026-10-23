using UnifierTSL.Module;
[assembly: RequiresCoreModule("RuntimeFixture.CoreModule")]
namespace RuntimeFixture.ChildModule;
public static class Entry {
    public static int Tick() => CoreModule.Entry.Tick();
}
