using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MapStudio;
namespace Pigeon.Manager.Desktop;
public sealed partial class MainWindow {
    async Task Smoke(string output) {
        output=Path.GetFullPath(output);if(Directory.Exists(output))throw new IOException("不得覆盖UI证据");Directory.CreateDirectory(output);
        var checks=new List<object>();int passed=0,failed=0;
        void Check(string name,bool pass){checks.Add(new{name,pass});if(pass)passed++;else failed++;}
        async Task Snap(string name){await Task.Delay(300);double scale=RenderScaling;using var bitmap=new RenderTargetBitmap(new PixelSize((int)(Bounds.Width*scale),(int)(Bounds.Height*scale)),new Vector(96*scale,96*scale));bitmap.Render(this);bitmap.Save(Path.Combine(output,name+".png"),PngBitmapEncoderOptions.Default);}
        string? fixture=null;int fi=Array.IndexOf(Program.Args,"--probe-package");if(fi>=0&&Program.Args.Length>fi+1)fixture=Program.Args[fi+1];
        bool canFixture=fixture!=null&&File.Exists(Path.Combine(workspace.Root,".manager-ui-fixture"))&&File.Exists(Path.Combine(fixture,"Manager.ConsoleProbe.dll"))&&AssemblyName.GetAssemblyName(Path.Combine(fixture,"Manager.ConsoleProbe.dll")).Name=="Manager.ConsoleProbe";
        var started=new List<string>();
        try{
            Check("actual-avalonia-dark-window",IsVisible&&ActualThemeVariant==ThemeVariant.Dark);
            Check("no-default-production-roster",states.Length==0);
            Check("no-start-when-empty",!StartButton.IsEnabled);
            await Snap("01-empty-server-workspace");
            if(canFixture){
                for(int i=1;i<=2;i++){var imported=await client.Send(new("import",Value:fixture),60_000);Check("fixture-package-import-"+i,imported.Ok);await RefreshState();var p=states.Last().Profile;var r=await client.Send(new("save-profile",p.Id,Profile:p with{Name=i==1?"本地验证 · 西中东":"本地验证 · 流光城"}));Check("per-instance-name-"+i,r.Ok);}
                await RefreshState();selectedId=states[0].Profile.Id;BuildServers();UpdateSelected();
                StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(1200);await RefreshState();Check("actual-start-button-starts-owned-probe",Selected?.Identity!=null);if(Selected?.Identity!=null)started.Add(selectedId);
                string second=states[1].Profile.Id;var result=await client.Send(new("start",second));Check("second-conpty-instance",result.Ok);if(result.Ok)started.Add(second);
                await client.Send(new("send",selectedId,"colors\r"));await client.Send(new("send",second,"colors\r"));
                await Task.Delay(1000);await RefreshState();await ReadTerminal(selectedId,PrimaryTerminal);
                Check("real-colored-terminal-model",PrimaryTerminal.Session?.PlainText().Contains("RED")==true);
                Check("real-chinese-terminal-model",PrimaryTerminal.Session?.PlainText().Contains("中文")==true);
                await Snap("02-single-live-console");
                SplitToggle.IsChecked=true;compareId=second;BuildTargets();await ReadTerminal(second,SecondaryTerminal);await Snap("03-two-live-consoles");
                Check("split-console-layout",SecondaryPane.IsVisible&&PrimaryTerminal.Bounds.Width>280&&SecondaryTerminal.Bounds.Width>280&&SecondaryTerminal.TranslatePoint(default,this)!.Value.X>PrimaryTerminal.TranslatePoint(default,this)!.Value.X+PrimaryTerminal.Bounds.Width);
                CommandInput.Text="UI-命令";SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(600);await ReadTerminal(selectedId,PrimaryTerminal);
                Check("actual-command-button-roundtrip",PrimaryTerminal.Session?.PlainText().Contains("ECHO:UI-命令")==true);
                Check("second-console-not-contaminated",SecondaryTerminal.Session?.PlainText().Contains("UI-命令")==false);
            }
            PluginsNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(300);Check("actual-plugins-tab",PluginsPage.IsVisible);await Snap("04-plugin-library");
            MapsNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(400);
            Check("actual-map-tab-same-window",MapsPage.IsVisible&&studio!=null&&MapsPage.Content==studio.View);
            Check("no-second-generator-window",(Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.Count==1);
            var mapView=studio!.View;
            var controller=(StudioWindow)typeof(StudioWorkspace).GetField("controller",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(studio)!;
            Check("embedded-generator-controls-present",new[]{"StyleChoice","LayoutChoice","SizeChoice","DifficultyChoice","FoundationChoice","RefreshChoice"}.All(name=>controller.FindControl<ComboBox>(name)!=null));
            Check("embedded-map-panes-visible",mapView.GetVisualDescendants().OfType<LiveMap>().Single().Bounds.Width>900);
            Check("embedded-root-map-output",mapView.GetVisualDescendants().OfType<TextBox>().Any(t=>t.Text?.StartsWith(workspace.Maps)==true));
            await Snap("05-map-generator-embedded");
            int pi=Array.IndexOf(Program.Args,"--panorama");if(pi>=0&&Program.Args.Length>pi+1&&File.Exists(Program.Args[pi+1])){
                var live=mapView.GetVisualDescendants().OfType<LiveMap>().Single();live.LoadPanorama(Program.Args[pi+1],new StudioOptions{Width=8400,Height=2400});Check("actual-saved-panorama-in-embedded-view",live.HasFrame);await Snap("06-map-panorama");
            }
            var create=typeof(StudioWindow).GetMethod("CreateSeedsWindow",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var seedWindow=(Window)create.Invoke(controller,null)!;var dialog=seedWindow.ShowDialog(this);await Task.Delay(250);
            Check("embedded-seed-dialog-actual-owner",seedWindow.Owner==this&&seedWindow.GetVisualDescendants().OfType<CheckBox>().Count()==46);seedWindow.Close();await dialog;
            var busy=studio.IsBusy;ServersNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check("switching-away-keeps-map-workspace",studio!=null&&studio.IsBusy==busy&&ServersPage.IsVisible);
            Width=1220;Height=800;await Task.Delay(350);await Snap("07-minimum-two-consoles");
            Check("minimum-size-no-toolbar-overlap",StartButton.Bounds.Width>60&&PrimaryTerminal.Bounds.Height>300&&StopButton.TranslatePoint(default,this)!.Value.X>StartButton.TranslatePoint(default,this)!.Value.X);
            Width=1510;Height=1000;await Task.Delay(100);
        }catch(Exception e){checks.Add(new{name="uncaught-smoke",pass=false,error=e.ToString()});failed++;}
        finally{
            foreach(string id in started){try{var r=await client.Send(new("stop",id),30_000);Check("fixture-cleanup-"+id[..6],r.Ok);}catch(Exception e){checks.Add(new{name="fixture-cleanup",pass=false,error=e.Message});failed++;}}
            await RefreshState();
            if(canFixture) {
                try { Check("fixture-host-shutdown-without-live-servers",(await client.Send(new("shutdown-host"))).Ok); }
                catch(Exception e) { checks.Add(new{name="fixture-host-shutdown",pass=false,error=e.Message});failed++; }
            }
            Json.Save(Path.Combine(output,"result.json"),new{passed,failed,checks,scope="actual native GUI + real ConPTY fixture processes + embedded upstream map view; not live production/game",productionTouched=false});
            if(Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)d.Shutdown(failed==0?0:1);
        }
    }
}
