using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MapStudio;
namespace Pigeon.Manager.Desktop;

public sealed partial class MainWindow : Window {
    readonly Workspace workspace;
    readonly HostClient client;
    readonly DispatcherTimer polling = new() { Interval = TimeSpan.FromMilliseconds(800) };
    ServerState[] states = [];
    string? selectedId;
    string signature = "", idsSignature = "";
    bool refreshing, rebuilding, busy, confirmingStopAll;
    StartupQueueState? startupQueue, stopQueue;
    StudioWorkspace? studio;
    WindowState stateBeforeFullscreen = WindowState.Normal;
    public MainWindow() {
        InitializeComponent(); workspace = new(Program.Root);
        client = new(workspace, Path.Combine(workspace.App, "host", "Pigeon.UTSLManager.Host.exe"));
        StatusText.Text = workspace.Root;
        VersionText.Text = $"流光工作室  ·  Pigeon-X  ·  {DisplayVersion}";
        StartButton.IsEnabled = StartAllButton.IsEnabled = StopButton.IsEnabled = StopAllButton.IsEnabled = RestartButton.IsEnabled = ProfileButton.IsEnabled = ServerFolderButton.IsEnabled = false;
        CreateConsolePanes();
        StartConsoleCollector();
        Opened += async (_, _) => {
            try { await client.EnsureStarted(); await RefreshState(); polling.Tick += async (_, _) => await RefreshState(); polling.Start();
                int index = Array.IndexOf(Program.Args, "--ui-smoke"); if (index >= 0 && Program.Args.Length > index+1) await Smoke(Program.Args[index+1]);
            } catch (Exception e) { HostStatus.Text = "后台未连接"; HostDot.Fill = Brush.Parse("#EEA68F"); StatusText.Text = e.Message; }
        };
        Closing += (_, e) => { if (studio?.IsBusy == true) { e.Cancel = true; studio.Cancel(); StatusText.Text = "地图生成取消中，所属进程退出后可关闭"; } };
        Closed += (_, _) => { polling.Stop(); consolePaint.Stop(); consoleCollector?.Dispose(); studio?.Dispose(); };
        KeyDown += (_, e) => { if(e.Key==Key.Escape && WindowState==WindowState.FullScreen){WindowState=stateBeforeFullscreen;e.Handled=true;} };
    }
    async Task RefreshState() {
        if (refreshing) return; refreshing = true;
        try {
            var reply = await client.Send(new("list")); if (!reply.Ok) throw new IOException(reply.Message);
            states = reply.Servers!; startupQueue = reply.StartupQueue; stopQueue = reply.StopQueue; HostStatus.Text = stopQueue?.Active == true ? "逐服保存停止中" : startupQueue?.Active == true ? "按顺序启动中" : "本地后台已连接"; HostDot.Fill = Brush.Parse("#77C7A4");
            string next = string.Join('|', states.Select(s => $"{s.Profile.Id}:{s.Profile.Name}:{s.State}:{s.Identity?.Pid}")) + ":" + ServerSearch.Text;
            if (signature != next) { signature = next; BuildServers(); }
            string newIds = string.Join('|', states.Select(s=>s.Profile.Id + ":" + s.Profile.Name));
            if (newIds != idsSignature) { idsSignature = newIds; BuildTargets(); }
            UpdateSelected();
            if (ServersPage.IsVisible) {
                await RefreshWorldConsoles();
            }
        } catch (Exception e) { HostStatus.Text = "后台连接中断"; HostDot.Fill = Brush.Parse("#DCB06B"); StatusText.Text = e.Message; }
        finally { refreshing = false; }
    }
    void BuildServers() {
        rebuilding = true;
        try {
            var entries = states.Where(s => string.IsNullOrWhiteSpace(ServerSearch.Text) || s.Profile.Name.Contains(ServerSearch.Text, StringComparison.OrdinalIgnoreCase)).Select(s => {
                var label = new StackPanel { Spacing = 5 };
                label.Children.Add(new TextBlock { Text = s.Profile.Folder.Split('.')[0] + ". " + s.Profile.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                label.Children.Add(new TextBlock { Text = StateName(s.State) + (s.Profile.Ports.Length > 0 ? "  ·  " + string.Join('/',s.Profile.Ports) : ""), FontSize = 10, Foreground = Brush.Parse(s.Identity != null ? "#8CCFB4" : "#8F93A2") });
                return new ListBoxItem { Content = label, Tag = s.Profile.Id };
            }).ToArray();
            ServerList.ItemsSource = entries;
            selectedId ??= states.FirstOrDefault()?.Profile.Id;
            ServerList.SelectedItem = entries.FirstOrDefault(e => (string?)e.Tag == selectedId);
            ServerCount.Text = states.Length.ToString();
        } finally { rebuilding = false; }
    }
    void BuildTargets() {
        if (libraryBusy) { libraryTargetsPending = true; return; }
        rebuilding = true;
        try {
            var targetId = (PluginTarget.SelectedItem as ComboBoxItem)?.Tag as string;
            PluginTarget.ItemsSource = states.Select(s=> new ComboBoxItem { Content = s.Profile.Name, Tag = s.Profile.Id }).ToArray();
            PluginTarget.SelectedItem = PluginTarget.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == (targetId ?? selectedId));
        } finally { rebuilding = false; }
        if (libraryInitialized && PluginsPage.IsVisible && runtimeTargetId != PluginTargetId) _ = LoadRuntimeTarget();
    }
    static string StateName(string state) => state switch {
        "RUNNING"=>"世界已就绪", "STARTING"=>"等待世界加载", "START_FAILED"=>"世界启动未就绪",
        "START_CANCELLED"=>"启动已取消", "SAVING"=>"保存世界中", "STOPPING"=>"停止中", "STOP_TIMEOUT"=>"停止超时",
        "STOP_SAVE_FAILED"=>"保存失败 · 已保留进程", "STOP_IDENTITY_CHANGED"=>"身份已变化 · 停止被拒绝", "STOP_FAILED"=>"停止失败 · 请核对进程",
        "STOPPED"=>"已停止", "EXTERNAL"=>"外部运行 · 未接管", "ERROR"=>"启动失败", _=>"未知状态 · " + state
    };
    ServerState? Selected => states.FirstOrDefault(s => s.Profile.Id == selectedId);
    void UpdateSelected() {
        var s = Selected;
        SelectedTitle.Text = s?.Profile.Name ?? "服务器工作区";
        SelectedSubtitle.Text = s == null ? "尚未导入服务器" : StateName(s.State) + "  /  " + s.Profile.Folder;
        ToolTip.SetTip(SelectedSubtitle, s == null ? null : "原服目录：" + (s.Profile.ExistingDirectory ?? Path.Combine(workspace.Servers, s.Profile.Folder))
            + (s.RuntimeDirectory == null ? "" : "\n本代运行目录：" + s.RuntimeDirectory)
            + (s.RuntimeManifest == null ? "" : "\n本代运行清单：" + s.RuntimeManifest));
        PidLabel.Text = "PID  " + (s?.Identity?.Pid.ToString() ?? "—");
        MemoryLabel.Text = "内存  " + (s?.Identity != null && s.MemoryBytes > 0 ? (s.MemoryBytes/1048576d).ToString("N0")+" MB" : "—");
        UptimeLabel.Text = "运行  " + (s?.Identity != null ? (DateTime.UtcNow-new DateTime(s.Identity.StartedUtcTicks,DateTimeKind.Utc)).ToString(@"hh\:mm\:ss") : "—");
        bool owned = s?.Identity != null && s.State != "EXTERNAL";
        bool queued = startupQueue?.Active == true || stopQueue?.Active == true;
        bool stopping = stopQueue?.Active == true || s?.State is "STOPPING" or "STOP_TIMEOUT";
        StartButton.IsEnabled = !busy && !queued && !stopping && s != null && s.Identity == null;
        StartAllButton.IsEnabled = !busy && !queued && states.Any(state => state.Identity == null && state.State is not ("STARTING" or "STOPPING" or "STOP_TIMEOUT" or "EXTERNAL"));
        StopButton.IsEnabled = RestartButton.IsEnabled = !busy && !queued && !stopping && owned;
        StopAllButton.IsEnabled = !busy && !confirmingStopAll && stopQueue?.Active != true && (startupQueue?.Active == true || states.Any(state => state.Identity != null && state.State != "EXTERNAL"));
        ProfileButton.IsEnabled = !busy && !queued && s != null;
        ServerFolderButton.IsEnabled = s != null;
        foreach (var pane in consolePanes) { pane.Send.IsEnabled = pane.Input.IsEnabled = !busy && !stopping && owned && (pane.IsHost || (pane.World != null && s?.State == "RUNNING")); }
        ForceButton.IsVisible = s?.State == "STOP_TIMEOUT" && !busy && !queued;
        StateHint.Text = stopQueue?.Active == true ? stopQueue.Message : startupQueue?.Active == true ? startupQueue.Message : s?.Error ?? (s?.Identity == null ? stopQueue?.Message ?? startupQueue?.Message ?? "服务器已停止" : "已核验启动时全部世界就绪；玩家实际进入仍以游戏测试为准");
        UpdateLibraryControls();
    }
    async void Resize(string? id,int columns,int rows) { if(id==null)return;try { await client.Send(new("resize",id,$"{columns}x{rows}")); } catch { } }
    async Task<Reply?> Act(Request request, string pending) {
        if(busy)return null;busy=true;StatusText.Text=pending;UpdateSelected();
        try { var r = await client.Send(request, request.Action is "stop" or "import" ? 600_000 : 30_000);if(r.StartupQueue is {} start)startupQueue=start;if(r.StopQueue is {} stop)stopQueue=stop;StatusText.Text=r.Message;UpdateSelected();if(!r.Ok) await Notice("操作未完成",r.Message);await RefreshState();return r; }
        catch(Exception e){StatusText.Text=e.Message;await Notice("操作失败",e.Message);return null;}
        finally{busy=false;UpdateSelected();}
    }
    async void StartClick(object? sender,RoutedEventArgs e){if(StartButton.IsEnabled && selectedId!=null)await Act(new("start",selectedId),"启动中…");}
    async void StartAllClick(object? sender,RoutedEventArgs e){if(StartAllButton.IsEnabled)await Act(new("start-all"), "按编号排队启动，等待每服全部世界就绪…");}
    const string StopMechanism = "先通过 REST 保存并验证 World saved，再发送一次 /off。等待 20 秒仍未退出时，复核 PID、启动时间和程序路径，仅终止匹配的单个进程（不终止子进程）。保存验证失败或身份不符则停止操作并报告。";
    async void StopAllClick(object? sender,RoutedEventArgs e) {
        if(!StopAllButton.IsEnabled || confirmingStopAll)return;
        confirmingStopAll=true;UpdateSelected();
        try {
            if(await Confirm("全部保存并停止",$"将取消当前启动队列，依次保存并停止管理器所属的服务器。\n\n{StopMechanism}\n\n确认后由后台执行，进度显示在状态栏。"))
                await Act(new("stop-all"),"已请求后台取消启动队列，逐服保存并停止…");
        } finally {confirmingStopAll=false;UpdateSelected();}
    }
    async void StopClick(object? sender,RoutedEventArgs e){if(StopButton.IsEnabled && Selected is {} s && await Confirm("保存并停止服务器",$"{s.Profile.Name}\nPID {s.Identity?.Pid}\n\n{StopMechanism}"))await Act(new("stop",s.Profile.Id),"验证世界保存并等待所属进程退出…");}
    async void RestartClick(object? sender,RoutedEventArgs e){if(!RestartButton.IsEnabled || Selected is not {} s || !await Confirm("保存后重新启动",$"{s.Profile.Name}\n\n{StopMechanism}\n\n仅停止成功后重新启动一次。"))return;var r=await Act(new("stop",s.Profile.Id),"验证世界保存并停止…");if(r?.Ok==true && startupQueue?.Active!=true && stopQueue?.Active!=true)await Act(new("start",s.Profile.Id),"重新启动中…");}
    async void ForceClick(object? sender,RoutedEventArgs e){if(Selected is not {} s || s.Identity==null)return;var p=s.Identity;if(await Confirm("强制停止单个进程",$"PID {p.Pid}\n{p.Executable}\nUTC {new DateTime(p.StartedUtcTicks,DateTimeKind.Utc):O}\n可能丢失未保存数据；不停止子进程、不按名称杀进程。"))await Act(new("force-stop",s.Profile.Id,Identity:p),"复核精确身份并停止…");}
    async void ImportClick(object? sender,RoutedEventArgs e) {
        if(busy)return;
        var folders=await StorageProvider.OpenFolderPickerAsync(new(){Title="选择完整 UTSL 服务端包（复制导入）",AllowMultiple=false});
        string? source=folders.FirstOrDefault()?.TryGetLocalPath();if(source==null)return;
        await Act(new("import",Value:source),"复制独立服务端包中…");
    }
    async void RegisterExistingClick(object? sender,RoutedEventArgs e) {
        if(busy)return;
        var folders=await StorageProvider.OpenFolderPickerAsync(new(){Title="选择现有 UTSL 服务端目录（原地登记）",AllowMultiple=false});
        string? source=folders.FirstOrDefault()?.TryGetLocalPath();if(source==null)return;
        await Act(new("register-existing",Value:source),"登记原目录…");
    }
    void ServerSelectionChanged(object? sender,SelectionChangedEventArgs e){if(rebuilding)return;selectedId=(ServerList.SelectedItem as ListBoxItem)?.Tag as string;worlds=[];activeWorld=0;ClearConsolePanes();UpdateSelected();}
    void ServerSearchChanged(object? sender,TextChangedEventArgs e){if(workspace!=null)BuildServers();}
    void Page(int page){ServersPage.IsVisible=page==0;PluginsPage.IsVisible=page==1;MapsPage.IsVisible=page==2;ServersNav.Classes.Set("active",page==0);PluginsNav.Classes.Set("active",page==1);MapsNav.Classes.Set("active",page==2);}
    void ServersClick(object? s,RoutedEventArgs e)=>Page(0);
    async void PluginsClick(object? s,RoutedEventArgs e){Page(1);await LoadPlugins();}
    void MapsClick(object? s,RoutedEventArgs e){
        try{if(studio==null){studio=new StudioWorkspace(this,Path.Combine(workspace.App,"mapstudio"),workspace.Maps);MapsPage.Content=studio.View;}Page(2);}
        catch(Exception ex){StatusText.Text=ex.Message;}
    }
    void ExportLogClick(object? s,RoutedEventArgs e){if(selectedId==null)return;string path=Path.Combine(workspace.Data,"logs",selectedId+".ansi.log");if(File.Exists(path))Open(path);else StatusText.Text="尚无日志";}
    static void Open(string path)=>Process.Start(new ProcessStartInfo(path){UseShellExecute=true})?.Dispose();
    void OpenRootClick(object? s,RoutedEventArgs e)=>Open(workspace.Root);
    void OpenLibraryClick(object? s,RoutedEventArgs e)=>Open(workspace.Library);
    void ServerFolderClick(object? s,RoutedEventArgs e){if(Selected is {} state)Open(workspace.ServerDirectory(state.Profile));}
    void CloseClick(object? s,RoutedEventArgs e)=>Close();
    void MinClick(object? s,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
    void MaxClick(object? s,RoutedEventArgs e)=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    void FullscreenClick(object? s,RoutedEventArgs e){
        if(WindowState==WindowState.FullScreen)WindowState=stateBeforeFullscreen;
        else{stateBeforeFullscreen=WindowState;WindowState=WindowState.FullScreen;}
    }
    void DragWindow(object? s,PointerPressedEventArgs e){
        for(var current=e.Source as StyledElement;current!=null;current=current.Parent)if(current is Button)return;
        if(e.GetCurrentPoint(this).Properties.IsLeftButtonPressed){if(e.ClickCount==2)MaxClick(s,e);else BeginMoveDrag(e);}
    }
    static string DisplayVersion {
        get {
            var version = typeof(MainWindow).Assembly.GetName().Version;
            return version == null ? "0.2" : $"{version.Major}.{version.Minor}";
        }
    }
    async void AboutClick(object? s,RoutedEventArgs e) {
        var panel = new StackPanel { Margin = new Thickness(26), Spacing = 16 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("56,*") };
        var icon = new Border {
            Width = 50, Height = 50, CornerRadius = new CornerRadius(12),
            Background = Brush.Parse("#2D3040"), ClipToBounds = true,
            Child = new Image {
                Source = new Bitmap(AssetLoader.Open(new Uri("avares://Pigeon.UTSLManager/Assets/Branding/UTSl-Manager.png"))),
                Stretch = Stretch.UniformToFill
            }
        };
        header.Children.Add(icon);
        var heading = new StackPanel { Spacing = 5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = "UTSL 服务器管理器", FontSize = 20, FontWeight = FontWeight.SemiBold });
        var versionLine = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 9 };
        versionLine.Children.Add(new Border {
            Background = Brush.Parse("#2C3850"), CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2),
            Child = new TextBlock { Text = "v" + DisplayVersion, FontSize = 11, Foreground = Brush.Parse("#AFC7FF") }
        });
        versionLine.Children.Add(new TextBlock { Text = "多服务器运行与地图工坊集成", FontSize = 11, Foreground = Brush.Parse("#8F93A2"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        heading.Children.Add(versionLine);
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        panel.Children.Add(header);
        panel.Children.Add(new Border { Height = 1, Background = Brush.Parse("#393B45") });
        var details = new StackPanel { Spacing = 10 };
        void Row(string label, string value) {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*") };
            row.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brush.Parse("#8F93A2"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var text = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            details.Children.Add(row);
        }
        Row("开发团队", "流光工作室");
        Row("作者", "流光-Pigeon-X");
        Row("地图工坊", "CNS-Team/UTSL-MapStudio");
        Row("参考项目", "CNS-Team/PGame-TSManager");
        Row("许可证", "GPL-3.0；第三方组件保留原有许可");
        panel.Children.Add(details);
        var close = new Button { Content = "关闭", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, MinWidth = 82 };
        close.Classes.Add("primary");
        panel.Children.Add(close);
        var window = Dialog("关于", panel, 560);
        close.Click += (_, _) => window.Close();
        await window.ShowDialog(this);
    }
    Window Dialog(string title,Control content,double width=500)=>new(){Title=title,Content=content,Width=width,SizeToContent=SizeToContent.Height,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Icon=Icon,Background=Brush.Parse("#222329")};
    async Task<bool> Confirm(string title,string message){bool result=false;var panel=new StackPanel{Margin=new(25),Spacing=18};panel.Children.Add(new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap});var buttons=new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=9,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Right};var no=new Button{Content="取消"};var yes=new Button{Content="确认"};yes.Classes.Add("primary");buttons.Children.Add(no);buttons.Children.Add(yes);panel.Children.Add(buttons);var w=Dialog(title,panel,590);no.Click+=(_,_)=>w.Close();yes.Click+=(_,_)=>{result=true;w.Close();};await w.ShowDialog(this);return result;}
    async Task Notice(string title,string message){var panel=new StackPanel{Margin=new(25),Spacing=18};panel.Children.Add(new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap});var close=new Button{Content="关闭",HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Right};panel.Children.Add(close);var w=Dialog(title,panel,590);close.Click+=(_,_)=>w.Close();await w.ShowDialog(this);}
    async void ProfileClick(object? s,RoutedEventArgs e){
        if(Selected is not {} state)return;
        var p=state.Profile;bool editable=state.Identity==null;
        var panel=new StackPanel{Margin=new(24),Spacing=10};TextBox Field(string label,string value,bool multi=false){panel.Children.Add(new TextBlock{Text=label,Foreground=Brush.Parse("#A4A7B7"),FontSize=11});var box=new TextBox{Text=value,IsReadOnly=!editable,AcceptsReturn=multi,Height=multi?105:double.NaN};panel.Children.Add(box);return box;}
        var name=Field("服务器名称",p.Name);var ports=Field("预检端口（不修改服务端配置）",string.Join(',',p.Ports));var arguments=Field("启动参数（每行一个参数）",string.Join('\n',p.Arguments),true);
        var headless=new CheckBox{Content="世界控制台不另弹窗口",IsChecked=p.HeadlessWorlds,IsEnabled=editable};panel.Children.Add(headless);
        var autoStart=new CheckBox{Content="管理后台启动时自动启动此服",IsChecked=p.AutoStart,IsEnabled=editable};panel.Children.Add(autoStart);
        var directory=new TextBlock{Text=workspace.ServerDirectory(p),TextWrapping=TextWrapping.Wrap,FontSize=10,Foreground=Brush.Parse("#9A9EAE")};panel.Children.Add(directory);
        var status=new TextBlock{Text=editable?"已停止":"运行中 · 只读",Foreground=Brush.Parse("#D7B27C")};panel.Children.Add(status);
        var actions=new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=8};var native=new Button{Content="原生配置…"};var cancel=new Button{Content="关闭"};var save=new Button{Content="保存启动配置",IsEnabled=editable};actions.Children.Add(native);actions.Children.Add(cancel);actions.Children.Add(save);panel.Children.Add(actions);var window=Dialog("启动设置",panel,650);
        native.Click+=async(_,_)=>await EditNativeConfig(p);cancel.Click+=(_,_)=>window.Close();save.Click+=async(_,_)=>{try{int[] parsed=(ports.Text??"").Split([',','，',' '],StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();var updated=p with{Name=name.Text?.Trim()??p.Name,Ports=parsed,Arguments=(arguments.Text??"").Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(a=>a.TrimEnd('\r')).ToArray(),HeadlessWorlds=headless.IsChecked==true,AutoStart=autoStart.IsChecked==true};var r=await client.Send(new("save-profile",p.Id,Profile:updated));status.Text=r.Message;if(r.Ok){window.Close();await RefreshState();}}catch(Exception ex){status.Text=ex.Message;}};await window.ShowDialog(this);
    }
    async Task EditNativeConfig(ServerProfile p){var r=await client.Send(new("read-config",p.Id));if(!r.Ok){await Notice("原生配置",r.Message);return;}var panel=new Grid{RowDefinitions=new("Auto,*,Auto"),Margin=new(20),RowSpacing=12};panel.Children.Add(new TextBlock{Text="config/config.json",Foreground=Brush.Parse("#A8B0C8")});var text=new TextBox{Text=r.Message,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,FontFamily=FontFamily.Parse("Cascadia Mono,Consolas"),IsReadOnly=states.First(x=>x.Profile.Id==p.Id).Identity!=null};Grid.SetRow(text,1);panel.Children.Add(text);var save=new Button{Content="备份并保存",IsEnabled=!text.IsReadOnly,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Right};Grid.SetRow(save,2);panel.Children.Add(save);var w=new Window{Title="原生配置",Width=860,Height=650,Content=panel,WindowStartupLocation=WindowStartupLocation.CenterOwner,Icon=Icon};save.Click+=async(_,_)=>{var response=await client.Send(new("save-config",p.Id,text.Text));if(response.Ok)w.Close();else await Notice("未保存",response.Message);};await w.ShowDialog(this);}
}
