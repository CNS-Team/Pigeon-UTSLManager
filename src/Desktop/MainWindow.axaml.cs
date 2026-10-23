using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MapStudio;
namespace Pigeon.Manager.Desktop;

public sealed partial class MainWindow : Window {
    readonly Workspace workspace;
    readonly HostClient client;
    readonly DispatcherTimer polling = new() { Interval = TimeSpan.FromMilliseconds(800) };
    readonly Dictionary<string, TerminalSession> terminals = [];
    ServerState[] states = [];
    string? selectedId, compareId;
    string signature = "", idsSignature = "";
    bool refreshing, rebuilding, busy;
    StudioWorkspace? studio;
    public MainWindow() {
        InitializeComponent(); workspace = new(Program.Root);
        client = new(workspace, Path.Combine(workspace.Root, "程序", "host", "Pigeon.UTSLManager.Host.exe"));
        StatusText.Text = workspace.Root;
        StartButton.IsEnabled = StopButton.IsEnabled = RestartButton.IsEnabled = ProfileButton.IsEnabled = ServerFolderButton.IsEnabled = SendButton.IsEnabled = false;
        PrimaryTerminal.ResizeTerminal = (c,r) => Resize(selectedId,c,r);
        SecondaryTerminal.ResizeTerminal = (c,r) => Resize(compareId,c,r);
        Opened += async (_, _) => {
            try { await client.EnsureStarted(); await RefreshState(); polling.Tick += async (_, _) => await RefreshState(); polling.Start();
                int index = Array.IndexOf(Program.Args, "--ui-smoke"); if (index >= 0 && Program.Args.Length > index+1) await Smoke(Program.Args[index+1]);
            } catch (Exception e) { HostStatus.Text = "后台未连接"; HostDot.Fill = Brush.Parse("#EEA68F"); StatusText.Text = e.Message; }
        };
        Closing += (_, e) => { if (studio?.IsBusy == true) { e.Cancel = true; studio.Cancel(); StatusText.Text = "地图生成取消中，所属进程退出后可关闭"; } };
        Closed += (_, _) => { polling.Stop(); studio?.Dispose(); };
    }
    async Task RefreshState() {
        if (refreshing) return; refreshing = true;
        try {
            var reply = await client.Send(new("list")); if (!reply.Ok) throw new IOException(reply.Message);
            states = reply.Servers!; HostStatus.Text = "本地后台已连接"; HostDot.Fill = Brush.Parse("#77C7A4");
            string next = string.Join('|', states.Select(s => $"{s.Profile.Id}:{s.Profile.Name}:{s.State}:{s.Identity?.Pid}")) + ":" + ServerSearch.Text;
            if (signature != next) { signature = next; BuildServers(); }
            string newIds = string.Join('|', states.Select(s=>s.Profile.Id + ":" + s.Profile.Name));
            if (newIds != idsSignature) { idsSignature = newIds; BuildTargets(); }
            UpdateSelected();
            if (ServersPage.IsVisible) {
                await ReadTerminal(selectedId, PrimaryTerminal);
                if (SplitToggle.IsChecked == true) await ReadTerminal(compareId, SecondaryTerminal);
            }
        } catch (Exception e) { HostStatus.Text = "后台连接中断"; HostDot.Fill = Brush.Parse("#DCB06B"); StatusText.Text = e.Message; }
        finally { refreshing = false; }
    }
    void BuildServers() {
        rebuilding = true;
        try {
            var entries = states.Where(s => string.IsNullOrWhiteSpace(ServerSearch.Text) || s.Profile.Name.Contains(ServerSearch.Text, StringComparison.OrdinalIgnoreCase)).Select(s => {
                var label = new StackPanel { Spacing = 5 };
                label.Children.Add(new TextBlock { Text = s.Profile.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
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
        rebuilding = true;
        try {
            CompareServer.ItemsSource = states.Select(s => new ComboBoxItem { Content = s.Profile.Name, Tag = s.Profile.Id }).ToArray();
            compareId ??= states.FirstOrDefault(s=>s.Profile.Id != selectedId)?.Profile.Id;
            CompareServer.SelectedItem = CompareServer.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == compareId);
            var targetId = (PluginTarget.SelectedItem as ComboBoxItem)?.Tag as string;
            PluginTarget.ItemsSource = states.Select(s=> new ComboBoxItem { Content = s.Profile.Name, Tag = s.Profile.Id }).ToArray();
            PluginTarget.SelectedItem = PluginTarget.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == (targetId ?? selectedId));
        } finally { rebuilding = false; }
    }
    static string StateName(string state) => state switch { "RUNNING"=>"进程在线", "STARTING"=>"启动中", "STOPPING"=>"停止中", "STOP_TIMEOUT"=>"停止超时", "EXTERNAL"=>"外部运行 · 未接管", "ERROR"=>"启动失败", _=>"已停止" };
    ServerState? Selected => states.FirstOrDefault(s => s.Profile.Id == selectedId);
    void UpdateSelected() {
        var s = Selected;
        SelectedTitle.Text = s?.Profile.Name ?? "服务器工作区";
        SelectedSubtitle.Text = s == null ? "尚未导入服务器" : StateName(s.State) + "  /  " + s.Profile.Folder;
        ConsoleTitle.Text = s == null ? "控制台" : s.Profile.Name + "  ·  " + (s.Identity?.Pid.ToString() ?? "离线");
        PidLabel.Text = "PID  " + (s?.Identity?.Pid.ToString() ?? "—");
        MemoryLabel.Text = "内存  " + (s?.Identity != null && s.MemoryBytes > 0 ? (s.MemoryBytes/1048576d).ToString("N0")+" MB" : "—");
        UptimeLabel.Text = "运行  " + (s?.Identity != null ? (DateTime.UtcNow-new DateTime(s.Identity.StartedUtcTicks,DateTimeKind.Utc)).ToString(@"hh\:mm\:ss") : "—");
        bool owned = s?.Identity != null && s.State != "EXTERNAL";
        StartButton.IsEnabled = !busy && s != null && s.Identity == null;
        StopButton.IsEnabled = RestartButton.IsEnabled = !busy && owned && s?.State != "STOPPING" && s?.State != "STOP_TIMEOUT";
        ProfileButton.IsEnabled = !busy && s != null;
        ServerFolderButton.IsEnabled = s != null;
        SendButton.IsEnabled = CommandInput.IsEnabled = !busy && owned;
        EmptyConsole.IsVisible = s == null || !terminals.TryGetValue(s.Profile.Id, out var model) || model.Cursor == 0;
        EmptyText.Text = s == null ? "尚未导入服务器" : s.Identity == null ? "服务器已停止" : s.State == "EXTERNAL" ? "外部进程 · 没有所属控制台" : "等待控制台输出";
        EmptyImportButton.IsVisible = s == null;
        ForceButton.IsVisible = s?.State == "STOP_TIMEOUT" && !busy;
        StateHint.Text = s?.Error ?? (s?.Identity == null ? "未自动启动任何服务器" : "仅验证进程身份，世界可玩状态以服务端日志为准");
    }
    TerminalSession Model(string id) {
        if (terminals.TryGetValue(id, out var model)) return model;
        model = new(); model.Reply += async text => { try { await client.Send(new("send",id,text)); } catch { } }; terminals[id] = model; return model;
    }
    async Task ReadTerminal(string? id, TerminalView view) {
        if (id == null || !states.Any(s=>s.Profile.Id==id)) { view.Session=null;view.Refresh();return; }
        var model = Model(id); var r = await client.Send(new("read",id,Cursor:model.Cursor));
        if (!r.Ok || r.Log == null) return;
        if (r.Log.Truncated) { terminals.Remove(id); model=Model(id); model.Feed("[manager] 已超出内存回放范围；完整日志见数据/logs\r\n"); }
        foreach (var chunk in r.Log.Chunks) model.Feed(chunk.Text);
        model.Cursor=r.Log.Cursor; view.Session=model; view.Refresh();
    }
    async void Resize(string? id,int columns,int rows) { if(id==null)return;try { await client.Send(new("resize",id,$"{columns}x{rows}")); } catch { } }
    async Task<Reply?> Act(Request request, string pending) {
        if(busy)return null;busy=true;StatusText.Text=pending;UpdateSelected();
        try { var r = await client.Send(request, request.Action is "stop" or "import" ? 600_000 : 30_000);StatusText.Text=r.Message;if(!r.Ok) await Notice("操作未完成",r.Message);await RefreshState();return r; }
        catch(Exception e){StatusText.Text=e.Message;await Notice("操作失败",e.Message);return null;}
        finally{busy=false;UpdateSelected();}
    }
    async void StartClick(object? sender,RoutedEventArgs e){if(selectedId!=null)await Act(new("start",selectedId),"启动中…");}
    async void StopClick(object? sender,RoutedEventArgs e){if(Selected is {} s && await Confirm("停止服务器",$"{s.Profile.Name}\nPID {s.Identity?.Pid}\n将发送一次 /off，最多等待20秒。"))await Act(new("stop",s.Profile.Id),"等待所属进程优雅退出…");}
    async void RestartClick(object? sender,RoutedEventArgs e){if(Selected is not {} s || !await Confirm("重新启动",$"{s.Profile.Name}\n仅在 /off 成功退出后启动一次，不自动强杀。"))return;var r=await Act(new("stop",s.Profile.Id),"等待优雅退出…");if(r?.Ok==true)await Act(new("start",s.Profile.Id),"重新启动中…");}
    async void ForceClick(object? sender,RoutedEventArgs e){if(Selected is not {} s || s.Identity==null)return;var p=s.Identity;if(await Confirm("强制停止单个进程",$"PID {p.Pid}\n{p.Executable}\nUTC {new DateTime(p.StartedUtcTicks,DateTimeKind.Utc):O}\n可能丢失未保存数据；不停止子进程、不按名称杀进程。"))await Act(new("force-stop",s.Profile.Id,Identity:p),"复核精确身份并停止…");}
    async void ImportClick(object? sender,RoutedEventArgs e) {
        if(busy)return;
        var folders=await StorageProvider.OpenFolderPickerAsync(new(){Title="选择完整 UTSL 服务端包（复制导入）",AllowMultiple=false});
        string? source=folders.FirstOrDefault()?.TryGetLocalPath();if(source==null)return;
        await Act(new("import",Value:source),"复制独立服务端包中…");
    }
    async Task SendCommand(string? id,TextBox box){if(id==null||string.IsNullOrWhiteSpace(box.Text))return;string value=box.Text;var r=await client.Send(new("send",id,value+"\r"));if(r.Ok)box.Text="";else StatusText.Text=r.Message;}
    async void SendClick(object? s,RoutedEventArgs e){try{await SendCommand(selectedId,CommandInput);}catch(Exception ex){StatusText.Text=ex.Message;}}
    async void CommandKeyDown(object? s,KeyEventArgs e){if(e.Key==Key.Enter){e.Handled=true;try{await SendCommand(selectedId,CommandInput);}catch(Exception ex){StatusText.Text=ex.Message;}}}
    async void CompareSendClick(object? s,RoutedEventArgs e){try{await SendCommand(compareId,CompareInput);}catch(Exception ex){StatusText.Text=ex.Message;}}
    async void CompareKeyDown(object? s,KeyEventArgs e){if(e.Key==Key.Enter){e.Handled=true;try{await SendCommand(compareId,CompareInput);}catch(Exception ex){StatusText.Text=ex.Message;}}}
    void ServerSelectionChanged(object? sender,SelectionChangedEventArgs e){if(rebuilding)return;selectedId=(ServerList.SelectedItem as ListBoxItem)?.Tag as string;PrimaryTerminal.Session=selectedId==null?null:Model(selectedId);UpdateSelected();}
    void ServerSearchChanged(object? sender,TextChangedEventArgs e){if(workspace!=null)BuildServers();}
    void CompareChanged(object? s,SelectionChangedEventArgs e){if(rebuilding)return;compareId=(CompareServer.SelectedItem as ComboBoxItem)?.Tag as string;SecondaryTerminal.Session=compareId==null?null:Model(compareId);}
    void SplitChanged(object? s,RoutedEventArgs e){if(Consoles==null)return;bool split=SplitToggle.IsChecked==true;Consoles.ColumnDefinitions[1].Width=split?new GridLength(1,GridUnitType.Star):new GridLength(0);Grid.SetColumn(SecondaryPane,1);SecondaryPane.IsVisible=split;if(split&&compareId==null){compareId=states.FirstOrDefault(s=>s.Profile.Id!=selectedId)?.Profile.Id;BuildTargets();}}
    void Page(int page){ServersPage.IsVisible=page==0;PluginsPage.IsVisible=page==1;MapsPage.IsVisible=page==2;ServersNav.Classes.Set("active",page==0);PluginsNav.Classes.Set("active",page==1);MapsNav.Classes.Set("active",page==2);}
    void ServersClick(object? s,RoutedEventArgs e)=>Page(0);
    async void PluginsClick(object? s,RoutedEventArgs e){Page(1);await LoadPlugins();}
    void MapsClick(object? s,RoutedEventArgs e){
        try{if(studio==null){studio=new StudioWorkspace(this,Path.Combine(workspace.Root,"程序","mapstudio"),workspace.Maps);MapsPage.Content=studio.View;}Page(2);}
        catch(Exception ex){StatusText.Text=ex.Message;}
    }
    async Task LoadPlugins(){try{var r=await client.Send(new("plugins"));if(!r.Ok)throw new IOException(r.Message);PluginCount.Text=$"{r.Plugins!.Length} 个插件";PluginList.ItemsSource=r.Plugins.Select(p=>{
        var grid=new Grid{ColumnDefinitions=new("*,180,125,150"),Margin=new(2,5)};var label=new StackPanel{Spacing=3};label.Children.Add(new TextBlock{Text=p.Name,FontWeight=FontWeight.SemiBold});label.Children.Add(new TextBlock{Text=p.FileName,FontSize=10,Foreground=Brush.Parse("#9397A5")});grid.Children.Add(label);
        foreach(var pair in new[]{(1,p.Version),(2,$"{p.Bytes/1024d:N1} KB"),(3,p.Sha256[..12])}){var t=new TextBlock{Text=pair.Item2,VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center,FontFamily=FontFamily.Parse("Cascadia Mono,Consolas"),FontSize=11};Grid.SetColumn(t,pair.Item1);grid.Children.Add(t);}return new ListBoxItem{Content=grid,Tag=p.FileName};}).ToArray();}catch(Exception ex){StatusText.Text=ex.Message;}}
    async void AddPluginsClick(object? s,RoutedEventArgs e){var files=await StorageProvider.OpenFilePickerAsync(new(){Title="导入托管插件 DLL",AllowMultiple=true,FileTypeFilter=[new FilePickerFileType("程序集"){Patterns=["*.dll"]}]});var paths=files.Select(f=>f.TryGetLocalPath()).Where(p=>p!=null).Cast<string>().ToArray();if(paths.Length==0)return;await Act(new("library-add",Files:paths),"验证插件元数据与SHA…");await LoadPlugins();}
    async void ApplyPluginsClick(object? s,RoutedEventArgs e){var id=(PluginTarget.SelectedItem as ComboBoxItem)?.Tag as string;var files=PluginList.SelectedItems?.Cast<ListBoxItem>().Select(i=>(string)i.Tag!).ToArray()??[];if(id==null||files.Length==0){StatusText.Text="请选择插件和目标服务器";return;}var state=states.Single(s=>s.Profile.Id==id);if(!await Confirm("同步到停止的服务器",$"{state.Profile.Name}\n{string.Join('\n',files)}\n将核SHA并保留原件备份；不会启动或热载。"))return;var r=await Act(new("apply-plugins",id,Files:files),"备份并同步…");PluginStatus.Text=r?.Message??"未完成";}
    async void CopyConsoleClick(object? s,RoutedEventArgs e){if(selectedId!=null&&terminals.TryGetValue(selectedId,out var model)&&Clipboard!=null)await Clipboard.SetValueAsync(DataFormat.Text,model.PlainText());}
    void ExportLogClick(object? s,RoutedEventArgs e){if(selectedId==null)return;string path=Path.Combine(workspace.Data,"logs",selectedId+".ansi.log");if(File.Exists(path))Open(path);else StatusText.Text="尚无日志";}
    static void Open(string path)=>Process.Start(new ProcessStartInfo(path){UseShellExecute=true})?.Dispose();
    void OpenRootClick(object? s,RoutedEventArgs e)=>Open(workspace.Root);
    void OpenLibraryClick(object? s,RoutedEventArgs e)=>Open(workspace.Library);
    void ServerFolderClick(object? s,RoutedEventArgs e){if(Selected is {} state)Open(workspace.ServerDirectory(state.Profile));}
    void CloseClick(object? s,RoutedEventArgs e)=>Close();
    void MinClick(object? s,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
    void MaxClick(object? s,RoutedEventArgs e)=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    void DragWindow(object? s,PointerPressedEventArgs e){if(e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)BeginMoveDrag(e);}
    async void AboutClick(object? s,RoutedEventArgs e)=>await Notice("关于","UTSL服务器管理器 0.1\n开发团队：流光工作室\n署名：流光-Pigeon-X\n地图工坊：CNS-Team/UTSL-MapStudio\n参考：CNS-Team/PGame-TSManager\nGPL-3.0；第三方组件保留原有许可。");
    Window Dialog(string title,Control content,double width=500)=>new(){Title=title,Content=content,Width=width,SizeToContent=SizeToContent.Height,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Icon=Icon,Background=Brush.Parse("#222329")};
    async Task<bool> Confirm(string title,string message){bool result=false;var panel=new StackPanel{Margin=new(25),Spacing=18};panel.Children.Add(new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap});var buttons=new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=9,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Right};var no=new Button{Content="取消"};var yes=new Button{Content="确认"};yes.Classes.Add("primary");buttons.Children.Add(no);buttons.Children.Add(yes);panel.Children.Add(buttons);var w=Dialog(title,panel,590);no.Click+=(_,_)=>w.Close();yes.Click+=(_,_)=>{result=true;w.Close();};await w.ShowDialog(this);return result;}
    async Task Notice(string title,string message){var panel=new StackPanel{Margin=new(25),Spacing=18};panel.Children.Add(new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap});var close=new Button{Content="关闭",HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Right};panel.Children.Add(close);var w=Dialog(title,panel,590);close.Click+=(_,_)=>w.Close();await w.ShowDialog(this);}
    async void ProfileClick(object? s,RoutedEventArgs e){
        if(Selected is not {} state)return;
        var p=state.Profile;bool editable=state.Identity==null;
        var panel=new StackPanel{Margin=new(24),Spacing=10};TextBox Field(string label,string value,bool multi=false){panel.Children.Add(new TextBlock{Text=label,Foreground=Brush.Parse("#A4A7B7"),FontSize=11});var box=new TextBox{Text=value,IsReadOnly=!editable,AcceptsReturn=multi,Height=multi?105:double.NaN};panel.Children.Add(box);return box;}
        var name=Field("服务器名称",p.Name);var ports=Field("预检端口（不修改服务端配置）",string.Join(',',p.Ports));var arguments=Field("启动参数（每行一个参数）",string.Join('\n',p.Arguments),true);
        var headless=new CheckBox{Content="世界控制台不另弹窗口",IsChecked=p.HeadlessWorlds,IsEnabled=editable};panel.Children.Add(headless);
        var directory=new TextBlock{Text=workspace.ServerDirectory(p),TextWrapping=TextWrapping.Wrap,FontSize=10,Foreground=Brush.Parse("#9A9EAE")};panel.Children.Add(directory);
        var status=new TextBlock{Text=editable?"已停止":"运行中 · 只读",Foreground=Brush.Parse("#D7B27C")};panel.Children.Add(status);
        var actions=new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=8};var native=new Button{Content="原生配置…"};var cancel=new Button{Content="关闭"};var save=new Button{Content="保存启动配置",IsEnabled=editable};actions.Children.Add(native);actions.Children.Add(cancel);actions.Children.Add(save);panel.Children.Add(actions);var window=Dialog("启动设置",panel,650);
        native.Click+=async(_,_)=>await EditNativeConfig(p);cancel.Click+=(_,_)=>window.Close();save.Click+=async(_,_)=>{try{int[] parsed=(ports.Text??"").Split([',','，',' '],StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();var updated=p with{Name=name.Text?.Trim()??p.Name,Ports=parsed,Arguments=(arguments.Text??"").Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(a=>a.TrimEnd('\r')).ToArray(),HeadlessWorlds=headless.IsChecked==true};var r=await client.Send(new("save-profile",p.Id,Profile:updated));status.Text=r.Message;if(r.Ok){window.Close();await RefreshState();}}catch(Exception ex){status.Text=ex.Message;}};await window.ShowDialog(this);
    }
    async Task EditNativeConfig(ServerProfile p){var r=await client.Send(new("read-config",p.Id));if(!r.Ok){await Notice("原生配置",r.Message);return;}var panel=new Grid{RowDefinitions=new("Auto,*,Auto"),Margin=new(20),RowSpacing=12};panel.Children.Add(new TextBlock{Text="config/config.json",Foreground=Brush.Parse("#A8B0C8")});var text=new TextBox{Text=r.Message,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,FontFamily=FontFamily.Parse("Cascadia Mono,Consolas"),IsReadOnly=states.First(x=>x.Profile.Id==p.Id).Identity!=null};Grid.SetRow(text,1);panel.Children.Add(text);var save=new Button{Content="备份并保存",IsEnabled=!text.IsReadOnly,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Right};Grid.SetRow(save,2);panel.Children.Add(save);var w=new Window{Title="原生配置",Width=860,Height=650,Content=panel,WindowStartupLocation=WindowStartupLocation.CenterOwner,Icon=Icon};save.Click+=async(_,_)=>{var response=await client.Send(new("save-config",p.Id,text.Text));if(response.Ok)w.Close();else await Notice("未保存",response.Message);};await w.ShowDialog(this);}
}
