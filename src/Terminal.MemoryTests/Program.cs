using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pigeon.Manager;
using Pigeon.Manager.Desktop;
using VtNetCore.VirtualTerminal.Model;

string output = Path.GetFullPath(args[0]);
bool baseline = args.Contains("--baseline");
Directory.CreateDirectory(output);
var checks = new List<object>();
var samples = new List<object>();
var retainedSamples = new List<long>();
int failures = 0;
void Check(string name, bool pass, object? detail = null) {
    checks.Add(new { name, pass, detail });
    if (!pass) failures++;
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name} {JsonSerializer.Serialize(detail)}");
}
object Input(string path) => new { path, bytes = new FileInfo(path).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
long Heap() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
string Corpus(int start, int count) {
    var s = new StringBuilder();
    for (int i = start; i < start + count; i++)
        s.Append($"\x1b[38;2;{i%255};180;90m\x1b[48;2;10;20;30mCOLOR_{i:D6} 中文 TRUECOLOR 1234567890\x1b[0m\r\n");
    return s.ToString();
}
void Sample(string name, TerminalSession? session, int lines, long start) {
    long heap = Heap(); using var p = Process.GetCurrentProcess(); p.Refresh();
    var sample = new { name, lines, heapBytes=heap, privateBytes=p.PrivateMemorySize64, workingSet=p.WorkingSet64,
        elapsedMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds, historyRows=session?.PlainText().Split('\n').Length, frameRevision=session?.Frame.Revision };
    samples.Add(sample); Console.WriteLine(JsonSerializer.Serialize(sample));
    retainedSamples.Add(heap);
    File.WriteAllText(Path.Combine(output,"samples.json"),JsonSerializer.Serialize(samples,new JsonSerializerOptions { WriteIndented=true }));
}
var assembly = typeof(TerminalColor).Assembly;
var surface = assembly.GetExportedTypes().SelectMany(t => new[]{"TYPE "+t.FullName}.Concat(t.GetMembers(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly)
    .Where(m=>m.MemberType is MemberTypes.Constructor or MemberTypes.Method or MemberTypes.Field or MemberTypes.Property or MemberTypes.Event)
    .Select(m=>t.FullName+" :: "+m.MemberType+" "+m))).Order(StringComparer.Ordinal).ToArray();
File.WriteAllLines(Path.Combine(output,"public-api.txt"),surface);
long started = Stopwatch.GetTimestamp();
var regexFields = typeof(TerminalColor).GetFields(BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic).Where(f=>f.FieldType==typeof(Regex)).ToArray();
Check("regex-ownership-observed",regexFields.Length==2&&regexFields.All(f=>f.IsStatic!=baseline),regexFields.Select(f=>new{f.Name,f.IsStatic,f.IsInitOnly}));
var session = new TerminalSession(); session.SetViewport(140,40,0);session.Apply(null);
Sample("empty",session,0,started);
session.Apply(new LogPage(1,false,[new LogChunk(1,Corpus(0,40))]));
Sample("same-40-lines",session,40,started);
Check("same-truecolor-output",session.PlainText().Contains("COLOR_000039")&&session.Frame.Lines.SelectMany(l=>l).Any(s=>s.Foreground=="#27b45a"||s.Foreground=="#27B45A"));
{
    var a = new TerminalColor("#abcdef"); var b = new TerminalColor(a); var c = new TerminalColor("rgbi:ab/cd/ef");
    Check("web-rgbi-copy-preserve-color",a.ARGB==b.ARGB&&a.ARGB==c.ARGB);
    session.Apply(new LogPage(2,false,[new LogChunk(2,"\x1b[2J\x1b[H\x1b[31mRED\x1b[0m\r\n\x1b[38;5;196mINDEX\x1b[0m\r\nPROGRESS 1%\r\x1b[2KPROGRESS 99%\r\n\x1b[1;4;3mSTYLE\x1b[0m\r\n") ]));
    string text = session.PlainText();
    Check("ansi-cr-erase-cursor",text.Contains("PROGRESS 99%")&&!text.Contains("PROGRESS 1%"));
    // Both the shipped library and pinned upstream renderer report Italic=false;
    // preserve that pre-existing behavior instead of claiming unsupported italics.
    Check("ansi-basic-indexed-bold-underline-existing-italic-contract",session.Frame.Lines.SelectMany(l=>l).Any(s=>s.Text.Contains("STYLE")&&s.Bold&&!s.Italic&&s.Underline)
        &&session.Frame.Lines.SelectMany(l=>l).Any(s=>s.Text.Contains("INDEX")&&s.Foreground.Equals("#ff0000",StringComparison.OrdinalIgnoreCase)),session.Frame.Lines.SelectMany(l=>l).Where(s=>s.Text.Contains("STYLE")||s.Text.Contains("INDEX")).ToArray());
}
if (!baseline) {
    for(int batch=0;batch<24;batch++) {
        session.Apply(new LogPage(batch+3,false,[new LogChunk(batch+3,Corpus(40+batch*500,500))]));
        if(batch%4==3)Sample("history-"+(batch+1),session,40+(batch+1)*500,started);
    }
    Check("history-bounded",session.PlainText().Split('\n').Length<=2041,session.PlainText().Split('\n').Length);
    Check("history-newest-kept-oldest-evicted",session.PlainText().Contains("COLOR_012039")&&!session.PlainText().Contains("COLOR_000039"));
    long retained=Heap();Check("truecolor-retained-below-160MiB",retained<160L*1024*1024,retained);
    var plateau=retainedSamples.Skip(2).ToArray();
    Check("evicted-history-memory-plateau",plateau.Max()-plateau.Min()<8L*1024*1024,new{minimum=plateau.Min(),maximum=plateau.Max()});
    var terminalField=typeof(TerminalSession).GetField("terminal",BindingFlags.NonPublic|BindingFlags.Instance)!;
    object oldTerminal=terminalField.GetValue(session)!;
    session.Apply(new LogPage(99,true,[new LogChunk(99,"NEW_GENERATION\r\n")]));
    Check("truncation-resets-parser",!ReferenceEquals(oldTerminal,terminalField.GetValue(session))&&!session.PlainText().Contains("COLOR_012039")&&session.PlainText().Contains("NEW_GENERATION"));
    var dead=CreateReleasedSession();Heap();Check("released-session-collected",!dead.IsAlive);
    Sample("after-generation-reset",session,12040,started);
    if(args.Contains("--soak")) {
        var sessions=Enumerable.Range(0,3).Select(_=>new TerminalSession()).ToArray();
        var measurements=new List<object>();var privateSamples=new List<long>();
        var soak=Stopwatch.StartNew();
        for(int batch=0;batch<240;batch++) {
            foreach(var console in sessions)console.Apply(new LogPage(batch+1,false,[new LogChunk(batch+1,Corpus(batch*100,100))]));
            // No forced GC in the sustained workload: observe the runtime's own collector.
            if(batch%10==0) {
                using var p=Process.GetCurrentProcess();p.Refresh();privateSamples.Add(p.PrivateMemorySize64);
                measurements.Add(new { batch,elapsedMs=soak.ElapsedMilliseconds,managedBytes=GC.GetTotalMemory(false),
                    gcHeapBytes=GC.GetGCMemoryInfo().HeapSizeBytes,privateBytes=p.PrivateMemorySize64,workingSet=p.WorkingSet64,gen2=GC.CollectionCount(2) });
                File.WriteAllText(Path.Combine(output,"soak-samples.json"),JsonSerializer.Serialize(measurements,new JsonSerializerOptions{WriteIndented=true}));
            }
            await Task.Delay(250);
        }
        Check("three-console-72000-line-soak-history-bounded",sessions.All(s=>s.PlainText().Split('\n').Length<=2041));
        Check("soak-private-under-512MiB",privateSamples.Max()<512L*1024*1024,new{maximum=privateSamples.Max(),durationMs=soak.ElapsedMilliseconds});
        var tail=privateSamples.Skip(8).ToArray();
        Check("soak-private-plateau-after-warmup",tail.Max()-tail.Min()<128L*1024*1024,new{minimum=tail.Min(),maximum=tail.Max()});
        GC.KeepAlive(sessions);
    }
}
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{
    baseline,passed=checks.Count-failures,failed=failures,checks,samples,
    inputs=new[]{Input(assembly.Location),Input(typeof(TerminalSession).Assembly.Location),Input(typeof(LogPage).Assembly.Location)},
    boundary="Offline actual TerminalSession source and VT DLL; forced GC is measurement-only, never product behavior; no game or production",
},new JsonSerializerOptions{WriteIndented=true}));
return failures==0?0:1;

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference CreateReleasedSession() {
    var session=new TerminalSession();session.Apply(new LogPage(1,false,[new LogChunk(1,"\x1b[38;2;1;2;3mRELEASE\x1b[0m\r\n")]));return new(session);
}
