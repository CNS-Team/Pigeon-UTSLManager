using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

string runtime=Path.GetFullPath(args[0]),outFile=Path.GetFullPath(args[1]);
AssemblyLoadContext.Default.Resolving+=(_,name)=>File.Exists(Path.Combine(runtime,name.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(runtime,name.Name+".dll")):null;
string pipeName="UTSLFixture_"+Guid.NewGuid().ToString("N");Environment.SetEnvironmentVariable("UTSL_MANAGER_PIPE",pipeName);
var asm=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(runtime,"UnifierTSL.dll"));
var checks=new List<object>();int passed=0,failed=0;
void Check(string name,bool pass){checks.Add(new{name,pass});if(pass)passed++;else failed++;Console.WriteLine((pass?"PASS ":"FAIL ")+name);}
var worldType=asm.GetType("UnifierTSL.Servers.ServerContext")!;var running=worldType.GetProperty("IsRunning")!;
object World(string name){var w=RuntimeHelpers.GetUninitializedObject(worldType);var f=worldType.GetField("Name",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(f!=null)f.SetValue(w,name);else{var p=worldType.GetProperty("Name")!;p.SetValue(w,name);}return w;}
var startup=asm.GetType("UnifierTSL.Launcher.AutoStartServers")!;
var wait=startup.GetMethod("WaitForStartup",BindingFlags.NonPublic|BindingFlags.Static)!;
Exception? InvokeWait(object w,Thread t,TimeSpan timeout){try{wait.Invoke(null,new[]{w,t,timeout});return null;}catch(TargetInvocationException ex){return ex.InnerException;}}
using(var hold=new ManualResetEventSlim()){
    object w=World("slow");var t=new Thread(()=>hold.Wait()){IsBackground=true};t.Start();
    var set=Task.Run(async()=>{await Task.Delay(180);running.SetValue(w,true);});var elapsed=Stopwatch.StartNew();var error=InvokeWait(w,t,TimeSpan.FromSeconds(2));
    Check("actual-dll-waits-until-world-ready",error==null&&elapsed.ElapsedMilliseconds>=140);hold.Set();t.Join();await set;
}
using(var hold=new ManualResetEventSlim()){
    object w=World("timeout");var t=new Thread(()=>hold.Wait()){IsBackground=true};t.Start();var error=InvokeWait(w,t,TimeSpan.FromMilliseconds(120));Check("actual-dll-timeout-stops-next-world",error is TimeoutException);hold.Set();t.Join();
}
{object w=World("dead");var t=new Thread(()=>{}){IsBackground=true};t.Start();t.Join();Check("actual-dll-early-exit-rejected",InvokeWait(w,t,TimeSpan.FromSeconds(1)) is InvalidOperationException);}
Check("running-state-volatile-publication",worldType.GetField("isRunning",BindingFlags.NonPublic|BindingFlags.Instance)!.GetRequiredCustomModifiers().Any(t=>t.Name=="IsVolatile"));
var hubType=asm.GetType("UnifierTSL.Surface.Hosting.Unified.UnifiedConsoleHub")!;var hub=hubType.GetProperty("Instance")!.GetValue(null)!;
var register=hubType.GetMethod("Register")!;var write=hubType.GetMethod("Write")!;var read=hubType.GetMethod("ReadLineFor")!;var unregister=hubType.GetMethod("Unregister")!;
var worlds=new[]{World("测试·西"),World("测试"),World("测试·东")};foreach(var w in worlds){register.Invoke(hub,new[]{w});running.SetValue(w,true);}
async Task<JsonDocument> Call(object request){using var cancel=new CancellationTokenSource(5000);using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);await pipe.ConnectAsync(cancel.Token);using var writer=new StreamWriter(pipe,new UTF8Encoding(false),leaveOpen:true){AutoFlush=true};using var reader=new StreamReader(pipe,Encoding.UTF8,false,leaveOpen:true);await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(),cancel.Token);return JsonDocument.Parse((await reader.ReadLineAsync(cancel.Token))!);}
using(var reply=await Call(new{Action="read"})){Check("actual-dll-three-world-local-ipc",reply.RootElement.GetProperty("Ok").GetBoolean()&&reply.RootElement.GetProperty("Worlds").GetArrayLength()==3);}
write.Invoke(hub,new[]{worlds[0],"\u001b[31mWEST中文\u001b[0m",(object)true});write.Invoke(hub,new[]{worlds[1],"CENTER中文",(object)true});write.Invoke(hub,new[]{worlds[2],"EAST中文",(object)true});
foreach(string name in new[]{"测试·西","测试","测试·东"}){using var log=await Call(new{Action="read",World=name});string text=string.Concat(log.RootElement.GetProperty("Log").GetProperty("Chunks").EnumerateArray().Select(c=>c.GetProperty("Text").GetString()));Check("actual-dll-output-exact-"+name,name=="测试·西"?text.Contains("WEST中文")&&!text.Contains("CENTER中文")&&text.Contains('\u001b'):name=="测试"?text.Contains("CENTER中文")&&!text.Contains("WEST中文"):text.Contains("EAST中文")&&!text.Contains("CENTER中文"));}
using(var reply=await Call(new{Action="send",World="测试·东",Value="version"})){Check("actual-dll-nonactive-world-command-accepted",reply.RootElement.GetProperty("Ok").GetBoolean());}
var received=Task.Run(()=>read.Invoke(hub,new[]{worlds[2]}));Check("actual-dll-nonactive-world-read-without-switch",await received.WaitAsync(TimeSpan.FromSeconds(3)) is string value&&value=="version");
using(var reply=await Call(new{Action="send",World="missing",Value="version"})){Check("actual-dll-no-unknown-world-fallback",!reply.RootElement.GetProperty("Ok").GetBoolean());}
using(var reply=await Call(new{Action="send",World="测试",Value="version\r/off"})){Check("actual-dll-multiline-command-rejected",!reply.RootElement.GetProperty("Ok").GetBoolean());}
running.SetValue(worlds[1],false);using(var reply=await Call(new{Action="send",World="测试",Value="version"})){Check("actual-dll-stopped-world-input-rejected",!reply.RootElement.GetProperty("Ok").GetBoolean());}
Check("manager-host-command-stays-coordinator-scoped",!(bool)hubType.GetMethod("TryConsumeLauncherInput")!.Invoke(hub,new object?[]{"/off"})!);
unregister.Invoke(hub,new[]{worlds[0]});using(var reply=await Call(new{Action="send",World="测试·西",Value="version"})){Check("actual-dll-unregistered-world-rejected",!reply.RootElement.GetProperty("Ok").GetBoolean());}
foreach(var w in worlds.Skip(1))unregister.Invoke(hub,new[]{w});
var bytes=File.ReadAllBytes(Path.Combine(runtime,"UnifierTSL.dll"));File.WriteAllText(outFile,JsonSerializer.Serialize(new{passed,failed,sha256=Convert.ToHexString(SHA256.HashData(bytes)),bytes=bytes.Length,checks,scope="actual DLL startup wait + per-world hub/input/IPC; native map loading and player E2E not run"},new JsonSerializerOptions{WriteIndented=true}));return failed==0?0:1;