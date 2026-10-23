using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Pigeon.Manager;
using Pigeon.Manager.Desktop;

namespace Pigeon.Manager.Desktop {
    // Injected into the private copy only, after TerminalView's real draw loop.
    static class ConsoleBenchHooks {
        public sealed record Paint(long Count, TerminalFrame Frame, long Timestamp);
        static readonly ConcurrentDictionary<TerminalView, Paint> paints = new();
        public sealed record Applied(double DurationMs, long Timestamp, double ParseMs, double ViewportMs, double PlainTextMs);
        static readonly ConcurrentDictionary<TerminalSession, Applied> applies = new();
        public static void OnApplied(TerminalSession model, long start, long parsed, long framed) { long end = Stopwatch.GetTimestamp(); applies[model] = new(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, end,
            Stopwatch.GetElapsedTime(start, parsed).TotalMilliseconds,Stopwatch.GetElapsedTime(parsed, framed).TotalMilliseconds,Stopwatch.GetElapsedTime(framed, end).TotalMilliseconds); }
        public static Applied? LastApply(TerminalSession model) => applies.GetValueOrDefault(model);
        public static void OnRendered(TerminalView view, TerminalFrame frame) => paints.AddOrUpdate(view,
            _ => new(1, frame, Stopwatch.GetTimestamp()), (_, old) => new(old.Count + 1, frame, Stopwatch.GetTimestamp()));
        public static Paint? Latest(TerminalView view) => paints.GetValueOrDefault(view);
    }
}

sealed class BenchApp : Application {
    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; }
    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime) {
            var window = new BenchWindow(Entry.Output, Entry.Variant); lifetime.MainWindow = window;
            window.Opened += (_, _) => Dispatcher.UIThread.Post(async () => {
                int exit = await window.Run(); lifetime.Shutdown(exit);
            });
        }
        base.OnFrameworkInitializationCompleted();
    }
}

static class Entry {
    public static string Output = "", Variant = "";
    [STAThread] public static int Main(string[] args) {
        Output = Path.GetFullPath(args[0]); Variant = args[1];
        if (Directory.Exists(Output)) throw new IOException("Fresh evidence directory required");
        Directory.CreateDirectory(Output);
        return AppBuilder.Configure<BenchApp>().UsePlatformDetect().With(new Win32PlatformOptions {
            RenderingMode = [Win32RenderingMode.Software], CompositionMode = [Win32CompositionMode.RedirectionSurface], ShouldRenderOnUIThread = false
        }).StartWithClassicDesktopLifetime([]);
    }
}

sealed class BenchWindow : Window {
    readonly string output, variant;
    readonly TerminalView[] views = [new(), new(), new()];
    readonly TextBlock heading = new() { FontSize = 20, FontWeight = FontWeight.Bold };
    readonly TextBlock description = new() { Foreground = Brush.Parse("#B5B9CB"), TextWrapping = TextWrapping.Wrap };
    readonly TextBox input = new() { Text = "/help", PlaceholderText = "离线命令输入 · 不连接游戏" };
    readonly Button send = new() { Content = "发送到当前控制台" };
    readonly Grid consoles = new() { ColumnDefinitions = new("*") };
    readonly TextBlock metrics = new() { FontFamily = FontFamily.Parse("Cascadia Mono,Consolas,Microsoft YaHei UI"), TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#B7C9FF") };
    readonly List<object> checks = [], samples = [], semantics = [];
    readonly List<string> trace = [];
    ConsoleCollector collector = null!;
    FixtureBackend backend = null!;
    HostClient client = null!;
    readonly DispatcherTimer paint = new() { Interval = TimeSpan.FromMilliseconds(100) };
    ConsoleCollector.Batch? applied;
    long version, uiApplications;
    int active = -1, failures;
    bool triple;
    ProcessIdentity identity = new(Environment.ProcessId, DateTime.UtcNow.Ticks, "OFFLINE-CONSOLE-FIXTURE");
    readonly int uiThread = Environment.CurrentManagedThreadId;
    public BenchWindow(string output, string variant) {
        this.output = output; this.variant = variant;
        Title = "UTSL 控制台方案 A · TerminalView / VtNetCore · 离线基准"; Width = 1510; Height = 940; MinWidth = 1220; MinHeight = 800;
        Background = Brush.Parse("#17181D");
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto,Auto"), RowSpacing = 14, Margin = new(20) };
        heading.Text = "路线 A · 真实 TerminalView + VtNetCore"; root.Children.Add(heading);
        description.Text = "冻结源码副本 / 本地命名管道夹具 / 后台 collector 与 VT / 单个 latest 帧 / 10ms UI 心跳";
        Grid.SetRow(description, 1); root.Children.Add(description);
        for (int i = 0; i < views.Length; i++) {
            var border = new Border { Child = views[i], Background = Brush.Parse("#101115"), BorderBrush = Brush.Parse("#3A3D48"), BorderThickness = new(1), CornerRadius = new(6), Margin = new(i == 0 ? 0 : 8,0,0,0), IsVisible = i == 0 };
            consoles.Children.Add(border);
        }
        Grid.SetRow(consoles, 2); root.Children.Add(consoles);
        var command = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        command.Children.Add(input); Grid.SetColumn(send, 1); command.Children.Add(send); Grid.SetRow(command, 3); root.Children.Add(command);
        Grid.SetRow(metrics,4); root.Children.Add(metrics); Content = root;
        send.Click += async (_, _) => {
            string? world = active < 0 ? null : FixtureBackend.Worlds[active].Name;
            string value = input.Text ?? "";
            var request = new Request(world == null ? "send" : "world-send", "fixture", world == null ? value + "\r" : value, World: world);
            var reply = await Task.Run(() => client.Send(request));
            if (reply.Ok) input.Text = "";
        };
        paint.Tick += (_, _) => ApplyLatest();
    }
    void Check(string name, bool pass, object? detail = null) {
        checks.Add(new { name, pass, detail }); if (!pass) failures++;
        Console.WriteLine((pass ? "PASS " : "FAIL ") + name + (detail == null ? "" : " " + Json.Encode(detail)));
        Json.Save(Path.Combine(output,"progress.json"),new { passed=checks.Count-failures, failed=failures, samples, checks });
    }
    async Task Wait(Func<bool> ready, int ms = 10_000) {
        var time = Stopwatch.StartNew(); while (!ready() && time.ElapsedMilliseconds < ms) await Task.Delay(5);
        if (!ready()) throw new TimeoutException("Bench wait timed out: " + description.Text);
    }
    static string Text(TerminalFrame frame) => string.Join('\n', frame.Lines.Select(row => string.Concat(row.Select(span => span.Text))));
    static string First(TerminalFrame frame) => frame.Lines.Select(row => string.Concat(row.Select(span => span.Text)).Trim()).FirstOrDefault(t => t.Length > 0) ?? "";
    static bool ViewportSettled(TerminalSession model) {
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        return Equals(typeof(TerminalSession).GetField("requested",flags)!.GetValue(model),typeof(TerminalSession).GetField("applied",flags)!.GetValue(model));
    }
    void Select(int selected, bool three = false, bool newGeneration = false) {
        active=selected; triple=three; if (newGeneration) identity=identity with { StartedUtcTicks=identity.StartedUtcTicks+1 };
        collector.Select(new(++version,"fixture","离线 fixture",identity,true,true,selected,three));
        applied=null;
        consoles.ColumnDefinitions=new(three?"*,*,*":"*");
        for(int i=0;i<views.Length;i++){consoles.Children[i].IsVisible=three||i==0;Grid.SetColumn(consoles.Children[i],three?i:0);}
    }
    void ApplyLatest() {
        var latest=collector.Latest; if(latest==null||latest.Version!=version||ReferenceEquals(latest,applied))return;
        applied=latest;uiApplications++;
        for(int i=0;i<views.Length;i++){views[i].Session=i<latest.Panes.Length?latest.Panes[i].Model:null;views[i].Refresh();}
    }
    async Task Painted(TerminalView view,string marker) => await Wait(()=>ConsoleBenchHooks.Latest(view) is {} p && Text(p.Frame).Contains(marker),20_000);
    async Task Snapshot(string name) {
        await Task.Delay(120);
        using var bitmap=new RenderTargetBitmap(new PixelSize((int)Bounds.Width,(int)Bounds.Height),new Vector(96,96));
        bitmap.Render(this);bitmap.Save(Path.Combine(output,name+".png"),PngBitmapEncoderOptions.Default);
    }
    static string Corpus(int count,string label) {
        var text=new StringBuilder();string[] colors=["36","32","33","35","1;34","38;2;245;150;90"];
        for(int i=1;i<=count;i++)text.Append($"\u001b[{colors[i%colors.Length]}m[Help] /cmd{i:D5}\u001b[0m 参数 中文说明 权限:guest {(i==count?"BENCH_END_"+label:"")}\r\n");
        return text.ToString();
    }
    async Task Measure(int count,int round) {
        string label=$"{count:D5}-r{round}";string corpus=Corpus(count,label),marker="BENCH_END_"+label;
        string raw=Path.Combine(output,"raw",label+".ansi.txt");await Task.Run(()=>{Directory.CreateDirectory(Path.GetDirectoryName(raw)!);File.WriteAllText(raw,corpus,new UTF8Encoding(false));});
        backend.ResetHost();Select(-1,newGeneration:true);await Wait(()=>views[0].Session!=null&&applied?.Version==version);
        await Task.Delay(100);
        var intervals=new List<double>();var ticks=new List<object>();long previous=Stopwatch.GetTimestamp();
        var heartbeat=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(10)};
        heartbeat.Tick+=(_,_)=>{long now=Stopwatch.GetTimestamp();double gap=Stopwatch.GetElapsedTime(previous,now).TotalMilliseconds;intervals.Add(gap);ticks.Add(new{timestamp=now,intervalMs=gap});previous=now;};
        heartbeat.Start();await Task.Delay(40);intervals.Clear();ticks.Clear();previous=Stopwatch.GetTimestamp();
        long renders=ConsoleBenchHooks.Latest(views[0])?.Count??0,applications=uiApplications,requests=collector.Requests,initialRevision=views[0].Session!.Frame.Revision,start=Stopwatch.GetTimestamp();
        backend.QueueHelp(corpus);input.Text="/help";send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Painted(views[0],marker);
        var lastPaint=ConsoleBenchHooks.Latest(views[0])!;
        double elapsed=Stopwatch.GetElapsedTime(start,lastPaint.Timestamp).TotalMilliseconds;
        await Task.Delay(35);heartbeat.Stop();
        var model=views[0].Session!;var gaps=intervals.Order().ToArray();
        var apply=ConsoleBenchHooks.LastApply(model)!;
        int retained=model.PlainText().Split('\n').Count(t=>t.Trim().Length>0);
        bool tail=Text(lastPaint.Frame).Contains(marker),offUi=model.ParserThreadId!=uiThread;
        var sample=new{lines=count,round,elapsedMs=elapsed,heartbeatIntervalMs=10,maxHeartbeatMs=gaps.LastOrDefault(),p95HeartbeatMs=gaps.Length==0?0:gaps[(int)((gaps.Length-1)*.95)],heartbeatSamples=gaps.Length,
            renderCalls=lastPaint.Count-renders,framePublications=model.Frame.Revision-initialRevision,totalFrameRevision=model.Frame.Revision,uiBatchApplications=uiApplications-applications,collectionNotifications=0,parsedChunks=model.ParseCount,collectorRequests=collector.Requests-requests,retainedLines=retained,
            historyLimit=2000,visibleRows=model.Frame.Lines.Length,autoBottom=tail,parserThread=model.ParserThreadId,uiThread,rawPath=raw,rawBytes=new FileInfo(raw).Length,rawSha256=Hashes.FileSha(raw),
            applyMs=apply.DurationMs,parseMs=apply.ParseMs,viewportMs=apply.ViewportMs,plainTextMs=apply.PlainTextMs,sendAcceptMs=Stopwatch.GetElapsedTime(start,backend.HelpAccepted).TotalMilliseconds,
            collectorWaitMs=Stopwatch.GetElapsedTime(backend.HelpAccepted,backend.LastDataRead).TotalMilliseconds,
            ipcAndApplyMs=Stopwatch.GetElapsedTime(backend.LastDataRead,apply.Timestamp).TotalMilliseconds,
            renderWaitMs=Stopwatch.GetElapsedTime(apply.Timestamp,lastPaint.Timestamp).TotalMilliseconds};
        samples.Add(sample);await Task.Run(()=>Json.Save(Path.Combine(output,"raw",label+".heartbeat.json"),ticks));
        trace.Add(Json.Encode(sample));Console.WriteLine(Json.Encode(sample));
        Check(label+"/terminal-tail-rendered",tail);
        Check(label+"/heartbeat-under-150ms",gaps.Length>=5&&gaps.Max()<150,new{maxMs=gaps.LastOrDefault()});
        if(count==1500)Check(label+"/complete-under-1s",elapsed<1000,new{elapsedMs=elapsed});
        Check(label+"/parser-background-and-history-bounded",offUi&&retained<=2000+model.Frame.Lines.Length,new{retained,model.ParserThreadId,uiThread});
        metrics.Text=$"{count:N0} 行 / round {round}  |  完成 {elapsed:F1} ms  |  10ms 心跳最大 {gaps.LastOrDefault():F1} ms  |  render {lastPaint.Count-renders} / 新帧 {model.Frame.Revision-initialRevision}  |  保留 {retained} 行  |  自动到底 {tail}";
        if(round==3)await Snapshot($"throughput-{count:D5}");
    }
    void Wheel(double amount) {
        var args=new PointerWheelEventArgs(views[0],new Pointer(1,PointerType.Mouse,true),this,new Point(50,100),0,new PointerPointProperties(),KeyModifiers.None,new Vector(0,amount)) { RoutedEvent=InputElement.PointerWheelChangedEvent };
        views[0].RaiseEvent(args);
    }
    async Task Semantics() {
        heading.Text="路线 A · 滚动暂停 / 恢复 / 世界隔离";
        var model=views[0].Session!;
        Check("ansi-colors-chinese-and-real-control",model.Frame.Lines.SelectMany(l=>l).Select(s=>s.Foreground).Distinct().Count()>=4&&model.PlainText().Contains("中文说明")&&views[0].GetType()==typeof(TerminalView));
        long beforeWheel=model.Frame.Revision;
        for(int i=0;i<4;i++){Wheel(5);await Task.Delay(20);}
        await Wait(()=>model.Frame.Revision>beforeWheel&&ViewportSettled(model));
        await Wait(()=>ConsoleBenchHooks.Latest(views[0])?.Frame.Revision==model.Frame.Revision);
        string before=First(model.Frame);long cursor=model.Cursor, beforeNewOutput=model.Frame.Revision;
        backend.Append(null,string.Concat(Enumerable.Range(1,25).Select(i=>$"\u001b[32mSCROLL_NEW_{i:D3} 新输出\u001b[0m\r\n")));
        await Wait(()=>model.Cursor>cursor&&model.Frame.Revision>beforeNewOutput);
        await Wait(()=>ConsoleBenchHooks.Latest(views[0])?.Frame.Revision==model.Frame.Revision);
        string after=First(model.Frame);bool remainsUp=!Text(model.Frame).Contains("SCROLL_NEW_025");
        Check("wheel-up-pauses-tail-follow",remainsUp,new{before,after});
        Check("paused-viewport-preserves-anchor",before==after,new{before,after});
        semantics.Add(new{kind="scroll-anchor",before,after,remainsUp,anchored=before==after});
        beforeNewOutput=model.Frame.Revision;Wheel(5);
        await Wait(()=>model.Frame.Revision>beforeNewOutput&&ViewportSettled(model));
        await Wait(()=>ConsoleBenchHooks.Latest(views[0])?.Frame.Revision==model.Frame.Revision);
        string secondUp=First(model.Frame);
        Check("wheel-up-after-new-output-moves-backward",string.CompareOrdinal(secondUp,after)<0,new{after,secondUp});
        description.Text="用户连续上翻后的真实帧；新增输出不得偷偷跳到尾部。锚点保持结果记录在 result.json。";
        await Snapshot("scroll-paused");
        Wheel(-10_000);await Painted(views[0],"SCROLL_NEW_025");
        backend.Append(null,"\u001b[36mFOLLOW_RESUMED 回到底部后自动跟随\u001b[0m\r\n");await Painted(views[0],"FOLLOW_RESUMED");
        Check("wheel-to-bottom-resumes-follow",true);await Snapshot("scroll-resumed");
        long idle=model.ParseCount,revision=model.Frame.Revision;await Task.Delay(600);
        Check("idle-does-not-reparse-or-republish-frame",idle==model.ParseCount&&revision==model.Frame.Revision);
        paint.Stop();long apps=uiApplications;
        for(int i=1;i<=5;i++){backend.Append(null,$"COALESCED_{i} 单个最新帧\r\n");await Task.Delay(270);}
        var pending=collector.Latest;bool stayed=uiApplications==apps;paint.Start();await Painted(views[0],"COALESCED_5");
        Check("paused-paint-has-one-replaceable-latest-not-ui-queue",stayed&&uiApplications-apps<=2&&pending?.Panes[0].Model==model,new{uiApplications=uiApplications-apps});
        backend.Append(null,"HOST_ONLY 宿主独立\r\n");
        for(int i=0;i<3;i++)backend.Append(FixtureBackend.Worlds[i].Name,$"WORLD_ONLY_{i} {FixtureBackend.Worlds[i].Name} 中文\r\n");
        Select(0,true);await Wait(()=>views.All(v=>v.Session!=null)&&applied?.Version==version);
        await Wait(()=>views.Select((v,i)=>Text(v.Session!.Frame).Contains($"WORLD_ONLY_{i}")).All(x=>x));
        Check("three-world-streams-isolated",views.Select((v,i)=>!v.Session!.PlainText().Contains("HOST_ONLY")&&Enumerable.Range(0,3).Where(j=>j!=i).All(j=>!v.Session.PlainText().Contains($"WORLD_ONLY_{j}"))).All(x=>x));
        input.Text="/help world-command 中文";send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>views[0].Session!.PlainText().Contains("ECHO:/help world-command 中文"));
        Check("world-command-targeted-and-other-worlds-clean",!views[1].Session!.PlainText().Contains("world-command")&&!views[2].Session!.PlainText().Contains("world-command")&&backend.Commands.Any(c=>c.Action=="world-send"&&c.World==FixtureBackend.Worlds[0].Name));
        description.Text="三个真实 TerminalView · 西 / 中 / 东，各自独立 TerminalSession；命令只回显到指定世界。";
        metrics.Text="世界隔离 PASS · 指定世界命令回显 PASS · 宿主独立 · 当前画面为本地离线命名管道夹具，非真实游戏 E2E。";
        await Snapshot("three-world-isolation");
        Select(2);await Painted(views[0],"WORLD_ONLY_2");Check("single-world-switch-reuses-correct-stream",!views[0].Session!.PlainText().Contains("WORLD_ONLY_0"));
        Select(-1);await Painted(views[0],"HOST_ONLY");Check("host-switch-restores-host-without-world-output",!views[0].Session!.PlainText().Contains("WORLD_ONLY_0"));
        input.Text="host-command 中文";send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Painted(views[0],"ECHO:host-command 中文");
        Check("host-command-input-keeps-carriage-return-contract",backend.Commands.Any(c=>c.Action=="send"&&c.Value=="host-command 中文\r"));
        metrics.Text="离线命令、ANSI/中文、宿主与三世界隔离、历史上限及 latest-only 验证完成；真实游戏 /help = UNVERIFIED。";
        await Snapshot("host-command-and-history");
    }
    public async Task<int> Run() {
        try {
            var workspace=new Workspace(Path.Combine(output,"fixture-workspace"));backend=new(workspace);client=new(workspace,"NEVER-START-A-HOST-IN-THIS-BENCH");collector=new(client);
            Select(-1);paint.Start();await Wait(()=>views[0].Session!=null);
            backend.Append(null,Corpus(120,"warmup"));await Painted(views[0],"BENCH_END_warmup");await Task.Delay(300);
            foreach(int count in new[]{1500,4000,10000})for(int round=1;round<=3;round++)await Measure(count,round);
            await Semantics();
        } catch(Exception ex) { Check("no-unhandled-bench-exception",false,ex.ToString());await File.WriteAllTextAsync(Path.Combine(output,"exception.txt"),ex.ToString()); }
        finally {
            paint.Stop();collector?.Dispose();backend?.Dispose();
            await File.WriteAllLinesAsync(Path.Combine(output,"samples.jsonl"),trace);
            Json.Save(Path.Combine(output,"result.json"),new {variant,passed=checks.Count-failures,failed=failures,samples,checks,semantics,
                framework="Avalonia 12.1.2 + VtNetCore 1.0.30 + actual custom TerminalView/ConsoleCollector source snapshot",
                rendering="Software renderer in a native Avalonia window; counters are draw-loop calls, not display scanout/GPU present events",
                timing="10ms DispatcherTimer, from local fixture /help send to final marker recorded after the actual TerminalView draw loop; screenshot capture excluded",
                fixture="Local named pipe replays preserved ANSI help-equivalent text; no real Host process/server or game client",realServerHelp="UNVERIFIED",productionTouched=false});
        }
        return failures==0?0:1;
    }
}

sealed class FixtureBackend : IDisposable {
    public static readonly WorldConsoleInfo[] Worlds=[new(1,"测试·西",true),new(2,"测试",true),new(3,"测试·东",true)];
    readonly Workspace workspace;
    readonly object sync=new();readonly CancellationTokenSource quit=new();
    readonly Dictionary<string,List<LogChunk>> logs=new(StringComparer.Ordinal) { ["host"]=[],["测试·西"]=[],["测试"]=[],["测试·东"]=[] };
    string? help;
    public long HelpAccepted, LastDataRead;
    public readonly ConcurrentQueue<Request> Commands=new();
    public FixtureBackend(Workspace workspace){this.workspace=workspace;_=Task.Run(Listen);}
    public void ResetHost(){lock(sync){logs["host"].Clear();help=null;}}
    public void QueueHelp(string corpus){lock(sync)help=corpus;}
    public void Append(string? world,string text){lock(sync){var log=logs[world??"host"];var lines=text.Split("\r\n",StringSplitOptions.None);for(int i=0;i<lines.Length-1;i+=64){string chunk=string.Join("\r\n",lines.Skip(i).Take(Math.Min(64,lines.Length-1-i)))+"\r\n";log.Add(new(log.Count+1,chunk));}}}
    Reply Handle(Request request){
        lock(sync){
            if(request.Action=="ping")return new(true);
            if(request.Action is "send" or "world-send"){
                Commands.Enqueue(request);if(request.Value?.Trim()=="/help"&&help is {} body){help=null;HelpAccepted=Stopwatch.GetTimestamp();Append(null,body);}else Append(request.World,"\u001b[33mECHO:"+request.Value?.TrimEnd('\r')+"\u001b[0m\r\n");return new(true);
            }
            if(request.Action=="world-consoles"&&request.World==null)return new(true,Worlds:Worlds);
            if(request.Action is "read" or "world-consoles"){
                var log=logs[request.World??"host"];long cursor=log.Count;var chunks=log.Where(c=>c.Sequence>request.Cursor||request.Cursor>cursor).ToArray();if(chunks.Length>0&&request.World==null)LastDataRead=Stopwatch.GetTimestamp();return new(true,Log:new(cursor,request.Cursor>cursor,chunks));
            }
            return new(false,"BENCH rejects all lifecycle/mutation actions");
        }
    }
    async Task Listen(){
        try{while(!quit.IsCancellationRequested){
            var pipe=new NamedPipeServerStream(workspace.Pipe,PipeDirection.InOut,16,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(quit.Token);
            _=Task.Run(async()=>{using(pipe)using(var reader=new StreamReader(pipe,Encoding.UTF8,false,leaveOpen:true))using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),leaveOpen:true){AutoFlush=true}){
                try{var request=Json.Decode<Request>((await reader.ReadLineAsync(quit.Token))!);var reply=Handle(request);await writer.WriteLineAsync(Json.Encode(reply));}
                catch(OperationCanceledException)when(quit.IsCancellationRequested){}
            }});
        }}catch(OperationCanceledException){}
    }
    public void Dispose()=>quit.Cancel();
}
