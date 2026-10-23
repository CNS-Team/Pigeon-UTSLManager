using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Pigeon.Manager;
namespace Pigeon.Manager.Desktop;
internal static class Program {
    public static string[] Args = [];
    public static string Root = "";
    public static bool SoftwareRendering;
    public static void StartupEvent(string name) {
        try { Directory.CreateDirectory(Path.Combine(Root,"Data")); File.AppendAllText(Path.Combine(Root,"Data","ui-startup.jsonl"), Json.Encode(new { at=DateTimeOffset.Now, pid=Environment.ProcessId, name, softwareRendering=SoftwareRendering })+Environment.NewLine); } catch { }
    }
    [STAThread] public static int Main(string[] args) {
        Args = args;
        int index = Array.IndexOf(args, "--root");
        string defaultRoot = string.Equals(Path.GetFileName(Environment.ProcessPath), "UTSl-Manager.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Environment.ProcessPath!)! : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        Root = index >= 0 && args.Length > index + 1 ? Path.GetFullPath(args[index + 1]) : defaultRoot;
        SoftwareRendering = args.Contains("--software-rendering", StringComparer.Ordinal);
        StartupEvent("app-start");
        try {
            var builder = AppBuilder.Configure<ManagerApp>().UsePlatformDetect();
            if (SoftwareRendering) builder.With(new Win32PlatformOptions {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface],
                ShouldRenderOnUIThread = false
            });
            return builder.LogToTrace().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e) { Directory.CreateDirectory(Path.Combine(Root, "Data")); File.AppendAllText(Path.Combine(Root, "Data", "ui-error.log"), e + Environment.NewLine); return 1; }
    }
}
public sealed partial class ManagerApp : Application {
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted() {
        Program.StartupEvent("framework-initialized");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) {
            d.MainWindow = new MainWindow();
            Program.StartupEvent("window-constructed");
            d.MainWindow.Opened += (_,_) => Program.StartupEvent("window-opened");
        }
        base.OnFrameworkInitializationCompleted();
    }
}
