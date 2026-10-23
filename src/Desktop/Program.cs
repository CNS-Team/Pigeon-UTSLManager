using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Pigeon.Manager;
namespace Pigeon.Manager.Desktop;
internal static class Program {
    public static string[] Args = [];
    public static string Root = "";
    [STAThread] public static int Main(string[] args) {
        Args = args;
        int index = Array.IndexOf(args, "--root");
        string defaultRoot = string.Equals(Path.GetFileName(Environment.ProcessPath), "UTSL服务器管理器.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Environment.ProcessPath!)! : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        Root = index >= 0 && args.Length > index + 1 ? Path.GetFullPath(args[index + 1]) : defaultRoot;
        try { return AppBuilder.Configure<ManagerApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args); }
        catch (Exception e) { Directory.CreateDirectory(Path.Combine(Root, "数据")); File.AppendAllText(Path.Combine(Root, "数据", "ui-error.log"), e + Environment.NewLine); return 1; }
    }
}
public sealed partial class ManagerApp : Application {
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted() { if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.MainWindow = new MainWindow(); base.OnFrameworkInitializationCompleted(); }
}
