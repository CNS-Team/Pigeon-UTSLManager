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
        var layouts=new List<object>();
        void Check(string name,bool pass){checks.Add(new{name,pass});if(pass)passed++;else failed++;}
        async Task<bool> WaitUntil(Func<bool> ready,int timeoutMs=15_000) {
            var elapsed=Stopwatch.StartNew();
            while(!ready() && elapsed.ElapsedMilliseconds<timeoutMs)await Task.Delay(80);
            return ready();
        }
        string[] fixtureWorldNames=["fixture·西","fixture","fixture·东"];
        bool FreshBatch(ConsoleCollector.Batch? previous)=>appliedBatch is {} batch && !ReferenceEquals(batch,previous) && batch.Version==consoleGeneration;
        bool WorldFrame(int paneIndex,string worldName)=>!consolePanes[paneIndex].IsHost && consolePanes[paneIndex].World==worldName && consolePanes[paneIndex].Terminal.Session?.PlainText().Contains($"世界 {worldName} 中文控制台")==true;
        bool ThreeWorldFrames()=>fixtureWorldNames.Select((name,index)=>consolePanes[index].Border.IsVisible && consolePanes[index].Terminal.Bounds.Width>250 && WorldFrame(index,name)).All(ready=>ready);
        Rect? Box(Visual visual)=>visual.TranslatePoint(default,this) is {} origin && visual.Bounds.Width>0 && visual.Bounds.Height>0 ? new Rect(origin,visual.Bounds.Size) : null;
        bool Inside(Visual child,Visual parent)=>Box(child) is {} c && Box(parent) is {} p && c.Left>=p.Left-1 && c.Top>=p.Top-1 && c.Right<=p.Right+1 && c.Bottom<=p.Bottom+1;
        bool Before(Visual left,Visual right)=>Box(left) is {} l && Box(right) is {} r && l.Right<=r.Left+1;
        bool Centered(Visual child,Visual parent,bool horizontal=true)=>Box(child) is {} c && Box(parent) is {} p && (!horizontal || Math.Abs(c.Center.X-p.Center.X)<=1.5) && Math.Abs(c.Center.Y-p.Center.Y)<=1.5;
        Visual? ButtonContent(Button button)=>button.Content as Visual ?? button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        void CheckToolbar(string prefix) {
            var buttons=new[]{StartButton,StopButton,RestartButton,StartAllButton,StopAllButton,ProfileButton,ServerFolderButton};
            Check(prefix+"-toolbar-buttons-inside-header",buttons.All(button=>Inside(button,ServerHeader)) && Inside(ServerToolbar,ServerHeader));
            Check(prefix+"-toolbar-button-order",buttons.Zip(buttons.Skip(1),(left,right)=>Before(left,right)).All(ok=>ok));
            Check(prefix+"-separate-action-groups",Before(SelectedActionsGroup,BulkActionsGroup) && Before(BulkActionsGroup,SettingsActionsGroup) && Centered(SelectedActionsGroup,ServerToolbar,false) && Centered(BulkActionsGroup,ServerToolbar,false) && Centered(SettingsActionsGroup,ServerToolbar,false));
            Check(prefix+"-title-does-not-overlap-toolbar",Before(SelectedTitle,ServerToolbar) && Before(SelectedSubtitle,ServerToolbar));
            Check(prefix+"-metrics-do-not-overlap-world-controls",Before(ServerMetrics,WorldControls) && Inside(WorldControls,ServerDetails));
            foreach(var button in buttons.Concat(new[]{WorldTab1,WorldTab2,WorldTab3})) {
                var content=ButtonContent(button);
                bool aligned=content!=null && Inside(content,button) && Centered(content,button);
                if(content is StackPanel row)aligned&=row.Children.All(child=>Centered(child,row,false));
                Check(prefix+"-"+button.Name+"-content-centered",aligned);
                var bounds=Box(button);var body=content==null?null:Box(content);
                layouts.Add(new{sample=prefix,button=button.Name,x=bounds?.X,y=bounds?.Y,width=bounds?.Width,height=bounds?.Height,contentOffsetX=body?.Center.X-bounds?.Center.X,contentOffsetY=body?.Center.Y-bounds?.Center.Y});
            }
            Check(prefix+"-single-start-all-entry-in-toolbar",ServersPage.GetVisualDescendants().OfType<Button>().Count(button=>button.Name=="StartAllButton")==1 && StartAllButton.GetVisualAncestors().Contains(ServerToolbar) && StopAllButton.GetVisualAncestors().Contains(ServerToolbar));
        }
        void CheckQueuePresentation(string prefix) {
            var savedStart=startupQueue;var savedStop=stopQueue;bool savedBusy=busy;
            try {
                busy=false;startupQueue=new(true,Message:"fixture startup queue");stopQueue=null;UpdateSelected();
                Check(prefix+"-startup-queue-blocks-duplicate-start",!StartButton.IsEnabled && !StartAllButton.IsEnabled && !RestartButton.IsEnabled && StopAllButton.IsEnabled);
                startupQueue=null;stopQueue=new(true,Message:"fixture stop queue");UpdateSelected();
                Check(prefix+"-stop-queue-blocks-start-and-repeat-stop",!StartButton.IsEnabled && !StartAllButton.IsEnabled && !StopButton.IsEnabled && !RestartButton.IsEnabled && !StopAllButton.IsEnabled && !ForceButton.IsVisible);
                Check(prefix+"-stop-queue-progress-visible",StateHint.Text==stopQueue.Message);
            } finally {startupQueue=savedStart;stopQueue=savedStop;busy=savedBusy;UpdateSelected();}
        }
        async Task Snap(string name){await Task.Delay(300);double scale=RenderScaling;using var bitmap=new RenderTargetBitmap(new PixelSize((int)(Bounds.Width*scale),(int)(Bounds.Height*scale)),new Vector(96*scale,96*scale));bitmap.Render(this);bitmap.Save(Path.Combine(output,name+".png"),PngBitmapEncoderOptions.Default);}
        async Task SnapWindow(Window window,string name){await Task.Delay(300);double scale=window.RenderScaling;using var bitmap=new RenderTargetBitmap(new PixelSize((int)(window.Bounds.Width*scale),(int)(window.Bounds.Height*scale)),new Vector(96*scale,96*scale));bitmap.Render(window);bitmap.Save(Path.Combine(output,name+".png"),PngBitmapEncoderOptions.Default);}
        string? fixture=null;int fi=Array.IndexOf(Program.Args,"--probe-package");if(fi>=0&&Program.Args.Length>fi+1)fixture=Program.Args[fi+1];
        bool canFixture=states.Length==0&&fixture!=null&&File.Exists(Path.Combine(workspace.Root,".manager-ui-fixture"))&&File.Exists(Path.Combine(fixture,"Manager.ConsoleProbe.dll"))&&AssemblyName.GetAssemblyName(Path.Combine(fixture,"Manager.ConsoleProbe.dll")).Name=="Manager.ConsoleProbe";
        var fixtureIds=new HashSet<string>();
        bool fixtureStopped=false;
        Window? StopAllDialog()=>(Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault(window=>window.Owner==this && window.Title=="全部保存并停止");
        async Task<Window> OpenStopAllDialog() {
            StopAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(!await WaitUntil(()=>StopAllDialog()!=null,3000))throw new IOException("全部停止按钮没有打开真实确认窗口");
            return StopAllDialog()!;
        }
        void RequireFixtureRoster() {
            if(!canFixture || fixtureIds.Count==0 || workspace.Load().Any(profile=>!fixtureIds.Contains(profile.Id) || profile.ExistingDirectory!=null))throw new IOException("拒绝对非本次导入的隔离 fixture 执行全停");
        }
        try{
            Check("actual-avalonia-dark-window",IsVisible&&ActualThemeVariant==ThemeVariant.Dark);
            Check("no-default-production-roster",states.Length==0);
            Check("no-start-or-stop-when-empty",!StartButton.IsEnabled && !StartAllButton.IsEnabled && !StopAllButton.IsEnabled);
            Check("only-explicit-stopped-state-reports-stopped",StateName("STOPPED")=="已停止" && new[]{"SAVING","START_CANCELLED","STOP_SAVE_FAILED","STOP_IDENTITY_CHANGED","STOP_FAILED","FUTURE_STATE"}.All(state=>StateName(state)!="已停止"));
            Check("fixture-requires-empty-marked-workspace",fixture==null || canFixture);
            Check("traffic-and-right-window-controls",TrafficControls.Children.Count==3 && WindowControls.Children.OfType<Button>().Count()==4);
            // CheckBox inherits Button; inspect the actual toolbar children explicitly.
            var worldControlChildren=WorldControls.Children.ToArray();
            Check("only-world-tabs-no-manual-host-entry",worldControlChildren.SequenceEqual(new Control[]{WorldTab1,WorldTab2,WorldTab3,TripleToggle})
                && worldControlChildren.OfType<CheckBox>().SequenceEqual(new[]{TripleToggle})
                && new[]{WorldTab1,WorldTab2,WorldTab3}.Select(button=>button.Tag?.ToString()).SequenceEqual(new[]{"0","1","2"}));
            MaximizeWindowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(250);
            Check("right-maximize-button",WindowState==WindowState.Maximized);
            MaximizeWindowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(150);
            Check("right-restore-button",WindowState==WindowState.Normal);
            FullscreenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(250);
            Check("right-fullscreen-button",WindowState==WindowState.FullScreen);
            FullscreenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(150);
            Check("fullscreen-restores-previous-state",WindowState==WindowState.Normal);
            MinimizeWindowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(250);
            Check("right-minimize-button",WindowState==WindowState.Minimized);WindowState=WindowState.Normal;await Task.Delay(200);
            await Snap("01-empty-server-workspace");
            CheckToolbar("normal-empty");
            if(canFixture){
                for(int i=1;i<=2;i++){
                    var imported=await client.Send(new("import",Value:fixture),60_000);Check("fixture-package-import-"+i,imported.Ok);await RefreshState();
                    if(!imported.Ok || !await WaitUntil(()=>states.Length==i))throw new IOException("fixture 导入未出现在服务器列表");
                    var p=states.Last().Profile;fixtureIds.Add(p.Id);
                    string dir = workspace.ServerDirectory(p); Directory.CreateDirectory(Path.Combine(dir,"config","TShockAPI"));
                    var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);socket.Start();int port=((System.Net.IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
                    File.WriteAllText(Path.Combine(dir,"config","config.json"),"{\"launcher\":{\"autoStartServers\":[{\"name\":\"fixture·西\"},{\"name\":\"fixture\"},{\"name\":\"fixture·东\"}]}}");
                    File.WriteAllText(Path.Combine(dir,"config","TShockAPI","config.json"),Json.Encode(new{RestApiPort=port,ApplicationRestTokens=new Dictionary<string,string>{["LOCAL_TEST_ONLY"]="fixture"}}));
                    File.WriteAllText(Path.Combine(dir,"probe-delay-ms.txt"),i==1?"1200":"0");
                    File.WriteAllText(Path.Combine(dir,"probe-off-delay-ms.txt"),i==1?"3000":"0");
                    var r=await client.Send(new("save-profile",p.Id,Profile:p with{Name=i==1?"本地验证 · 西中东 · 多世界服务器工具栏布局与长名称截断验证":"本地验证 · 流光城"}));Check("per-instance-name-"+i,r.Ok);
                }
                await RefreshState();selectedId=states[0].Profile.Id;BuildServers();UpdateSelected();
                CheckQueuePresentation("offline");
                StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("actual-start-button-queues-in-background",await WaitUntil(()=>startupQueue?.Active==true && !busy,5000));
                Check("actual-startup-queue-disables-start-buttons",!StartButton.IsEnabled && !StartAllButton.IsEnabled);
                if(!await WaitUntil(()=>Selected?.State=="RUNNING" && startupQueue?.Active!=true && !busy))throw new IOException("首个 fixture 未完成后台启动");
                Check("actual-start-button-starts-owned-probe",Selected?.Identity!=null);
                string second=states[1].Profile.Id;StartAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(!await WaitUntil(()=>states.All(state=>state.State=="RUNNING") && startupQueue?.Active!=true && !busy))throw new IOException("全部启动按钮未使第二个 fixture 就绪");
                Check("actual-start-all-button-starts-second-conpty-instance",states.Single(state=>state.Profile.Id==second).Identity!=null);
                await client.Send(new("send",selectedId,"colors\r"));await client.Send(new("send",second,"colors\r"));
                var beforeHostBatch=appliedBatch;activeWorld=-1;ClearConsolePanes();await RefreshWorldConsoles();
                Check("host-console-frame-arrives",await WaitUntil(()=>FreshBatch(beforeHostBatch) && consolePanes[0].IsHost && consolePanes[0].World==null && PrimaryTerminal.Session?.PlainText().Contains("RED")==true));
                Check("real-colored-terminal-model",PrimaryTerminal.Session?.PlainText().Contains("RED")==true);
                Check("real-chinese-terminal-model",PrimaryTerminal.Session?.PlainText().Contains("中文")==true);
                await PerformanceSmoke(Check, output, selectedId!);
                await Snap("02-single-live-console");
                var beforeTripleBatch=appliedBatch;activeWorld=0;TripleToggle.IsChecked=true;ClearConsolePanes();await RefreshWorldConsoles();
                Check("three-world-console-layout",await WaitUntil(()=>FreshBatch(beforeTripleBatch) && ThreeWorldFrames() && consolePanes.All(p=>p.Send.IsEnabled)));
                await Snap("03-three-world-consoles");CheckToolbar("normal-running");
                var beforeCommandBatch=appliedBatch;consolePanes[0].Input.Text="UI-命令";consolePanes[0].Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("actual-world-command-button-roundtrip",await WaitUntil(()=>FreshBatch(beforeCommandBatch) && ThreeWorldFrames() && PrimaryTerminal.Session?.PlainText().Contains("ECHO:UI-命令")==true));
                Check("other-worlds-not-contaminated",consolePanes.Skip(1).All(p=>p.Terminal.Session?.PlainText().Contains("UI-命令")==false));
                var beforeThirdBatch=appliedBatch;TripleToggle.IsChecked=false;WorldTab3.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("world-tab-three-single-view",await WaitUntil(()=>FreshBatch(beforeThirdBatch) && WorldFrame(0,fixtureWorldNames[2]) && consolePanes.Skip(1).All(p=>!p.Border.IsVisible)));
                var beforeSecondBatch=appliedBatch;WorldTab2.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("world-tab-two-single-view",await WaitUntil(()=>FreshBatch(beforeSecondBatch) && WorldFrame(0,fixtureWorldNames[1]) && consolePanes.Skip(1).All(p=>!p.Border.IsVisible)));
            }
            PluginsNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(300);Check("actual-plugins-tab",PluginsPage.IsVisible);await Snap("04-plugin-library");
            if (canFixture && Program.Args.Contains("--runtime-library-smoke")) await RuntimeLibrarySmoke(Check, Snap, output, fixtureIds.ToArray());
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
            var mapWasBusy=studio.IsBusy;ServersNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check("switching-away-keeps-map-workspace",studio!=null&&studio.IsBusy==mapWasBusy&&ServersPage.IsVisible);
            if(Program.Args.Contains("--about-smoke")){
                AboutClick(null,new RoutedEventArgs());
                Window? about=null;
                bool aboutOpened=await WaitUntil(()=> (about=(Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault(window=>window.Owner==this&&window.Title=="关于"))!=null,3000);
                Check("about-dialog-opens",aboutOpened);
                if(about!=null){
                    var aboutText=about.GetVisualDescendants().OfType<TextBlock>().Select(text=>text.Text??"").ToArray();
                    Check("about-version-display",aboutText.Contains("v0.2"));
                    Check("about-author-label",aboutText.Contains("作者")&&aboutText.Contains("流光-Pigeon-X"));
                    Check("about-team-label",aboutText.Contains("开发团队")&&aboutText.Contains("流光工作室"));
                    Check("about-no-legacy-signature-label",!aboutText.Contains("署名"));
                    await SnapWindow(about,"09-about");
                    about.Close();
                }
            }
            Width=1220;Height=800;
            Check("minimum-window-size-applied",await WaitUntil(()=>Math.Abs(Bounds.Width-1220)<=2 && Math.Abs(Bounds.Height-800)<=2,3000));
            if(canFixture){var beforeMinimumBatch=appliedBatch;TripleToggle.IsChecked=true;await RefreshWorldConsoles();Check("minimum-three-console-frames",await WaitUntil(()=>FreshBatch(beforeMinimumBatch) && ThreeWorldFrames()));}
            await Snap("07-minimum-toolbar-and-three-consoles");CheckToolbar("minimum-1220");
            Check("minimum-size-console-remains-usable",PrimaryTerminal.Bounds.Height>300);
            if(canFixture) {
                RequireFixtureRoster();CheckQueuePresentation("running");
                var identities=states.ToDictionary(state=>state.Profile.Id,state=>state.Identity);
                var cancelDialog=await OpenStopAllDialog();
                Check("stop-all-real-confirmation-required",cancelDialog.Owner==this && stopQueue?.Active!=true);
                Check("stop-all-confirmation-explains-save-and-single-pid-fallback",cancelDialog.GetVisualDescendants().OfType<TextBlock>().Any(text=>text.Text?.Contains("World saved")==true && text.Text.Contains("20 秒") && text.Text.Contains("不终止子进程")));
                cancelDialog.GetVisualDescendants().OfType<Button>().Single(button=>Equals(button.Content,"取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(()=>!confirmingStopAll,3000);await RefreshState();
                Check("stop-all-cancel-keeps-fixture-identities",states.All(state=>state.Identity!=null && state.Identity==identities[state.Profile.Id]));
                var confirmDialog=await OpenStopAllDialog();var submitted=Stopwatch.StartNew();
                confirmDialog.GetVisualDescendants().OfType<Button>().Single(button=>Equals(button.Content,"确认")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("stop-all-request-returns-to-ui-without-waiting-for-queue",await WaitUntil(()=>stopQueue?.Active==true && !busy && !confirmingStopAll,5000));
                layouts.Add(new{sample="stop-all-submit",elapsedMs=submitted.ElapsedMilliseconds});
                Check("stop-all-state-received-from-host",stopQueue!=null);
                Check("actual-stop-queue-disables-all-start-and-repeat-stop-actions",stopQueue?.Active==true && !StartButton.IsEnabled && !StartAllButton.IsEnabled && !RestartButton.IsEnabled && !StopButton.IsEnabled && !StopAllButton.IsEnabled);
                fixtureStopped=await WaitUntil(()=>stopQueue?.Active!=true && startupQueue?.Active!=true && states.All(state=>state.Identity==null),90_000);
                Check("stop-all-real-fixtures-exited",fixtureStopped);
                Check("stop-all-completion-unlocks-start",fixtureStopped && StartButton.IsEnabled && StartAllButton.IsEnabled && !StopAllButton.IsEnabled);
                await Snap("08-stop-all-completed");
            }
            Width=1510;Height=1000;await Task.Delay(100);
        }catch(Exception e){checks.Add(new{name="uncaught-smoke",pass=false,error=e.ToString()});failed++;}
        finally{
            if(canFixture) {
                try {
                    foreach(var window in (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.Where(window=>window.Owner==this).ToArray() ?? [])window.Close();
                    if(fixtureIds.Count>0 && !fixtureStopped) {
                        RequireFixtureRoster();var cleanup=await client.Send(new("stop-all"));Check("fixture-cleanup-stop-all-accepted",cleanup.Ok);
                        await RefreshState();
                        Check("fixture-cleanup-no-live-process-or-queue",await WaitUntil(()=>stopQueue?.Active!=true && startupQueue?.Active!=true && states.All(state=>state.Identity==null),90_000));
                    }
                    Check("fixture-host-shutdown-without-live-servers",(await client.Send(new("shutdown-host"))).Ok);
                } catch(Exception e) { checks.Add(new{name="fixture-cleanup",pass=false,error=e.Message});failed++; }
            }
            Json.Save(Path.Combine(output,"result.json"),new{passed,failed,checks,layouts,fixtureEnabled=canFixture,scope="actual native GUI geometry + confirmed stop-all + real ConPTY fixtures + embedded upstream map view; not live production/game",productionTouched=false});
            if(Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)d.Shutdown(failed==0?0:1);
        }
    }
}
