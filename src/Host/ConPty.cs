using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pigeon.Manager;
namespace Pigeon.Manager.Host;

sealed class ConPty : IDisposable {
    readonly FileStream input, output;
    readonly object writeLock = new();
    IntPtr console;
    public Process Process { get; }
    public ProcessIdentity Identity { get; }
    public ConPty(string executable, string workingDirectory, string[] arguments, bool headless, string? worldPipe = null) {
        IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero, outRead = IntPtr.Zero, outWrite = IntPtr.Zero, attr = IntPtr.Zero, env = IntPtr.Zero;
        Process? process = null;
        try {
            Check(CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0)); Check(CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0));
            int result = CreatePseudoConsole(new COORD(140, 40), inRead, outWrite, 0, out console);
            if (result != 0) Marshal.ThrowExceptionForHR(result);
            CloseHandle(inRead); inRead = IntPtr.Zero; CloseHandle(outWrite); outWrite = IntPtr.Zero;
            IntPtr bytes = IntPtr.Zero; InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            attr = Marshal.AllocHGlobal(bytes); Check(InitializeProcThreadAttributeList(attr, 1, 0, ref bytes));
            Check(UpdateProcThreadAttribute(attr, 0, (IntPtr)0x20016, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
            var start = new STARTUPINFOEX(); start.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>(); start.lpAttributeList = attr;
            var variables = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
            if (headless) variables["UTSL_CONSOLE_MODE"] = "headless";
            if (worldPipe != null) { variables["UTSL_MANAGER_PIPE"] = worldPipe; variables["UTSL_CONSOLE_MODE"] = "unified"; }
            variables["TERM"] = "xterm-256color";
            string block = string.Join('\0', variables.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e => e.Key + "=" + e.Value)) + "\0\0";
            env = Marshal.StringToHGlobalUni(block);
            var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
            Check(CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x80000 | 0x400, env, workingDirectory, ref start, out var info));
            try { process = Process.GetProcessById(info.dwProcessId); } finally { CloseHandle(info.hThread); CloseHandle(info.hProcess); }
            Process = process; Identity = new(process.Id, process.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(executable));
            input = new FileStream(new SafeFileHandle(inWrite, true), FileAccess.Write); inWrite = IntPtr.Zero;
            output = new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read); outRead = IntPtr.Zero;
        } catch { if (console != IntPtr.Zero) { ClosePseudoConsole(console); console = IntPtr.Zero; } process?.Dispose(); throw; }
        finally {
            if (attr != IntPtr.Zero) { DeleteProcThreadAttributeList(attr); Marshal.FreeHGlobal(attr); }
            if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
            foreach (var h in new[] { inRead, inWrite, outRead, outWrite }) if (h != IntPtr.Zero) CloseHandle(h);
        }
    }
    public Task Drain(Action<string> receive) => Task.Run(() => {
        var bytes = new byte[8192]; var chars = new char[8192]; var decoder = new UTF8Encoding(false, false).GetDecoder();
        try { int size; while ((size = output.Read(bytes)) > 0) { int count = decoder.GetChars(bytes, 0, size, chars, 0, false); if (count > 0) receive(new string(chars, 0, count)); } }
        catch (IOException) { } catch (ObjectDisposedException) { }
    });
    public void Write(string text) { lock (writeLock) { byte[] bytes = Encoding.UTF8.GetBytes(text); input.Write(bytes); input.Flush(); } }
    public void Resize(int columns,int rows) { if(console==IntPtr.Zero)return;int result=ResizePseudoConsole(console,new COORD((short)columns,(short)rows));if(result!=0)Marshal.ThrowExceptionForHR(result); }
    public void Dispose() { input.Dispose(); if (console != IntPtr.Zero) { var handle = console; console = IntPtr.Zero; ClosePseudoConsole(handle); } output.Dispose(); Process.Dispose(); }
    static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    static string Quote(string s) {
        var b = new StringBuilder("\""); int slash = 0;
        foreach (char c in s) { if (c == '\\') { slash++; continue; } if (c == '"') { b.Append('\\', slash * 2 + 1).Append(c); slash = 0; continue; } b.Append('\\', slash).Append(c); slash = 0; }
        return b.Append('\\', slash * 2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential)] struct COORD(short x, short y) { public short X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct STARTUPINFO { public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")] static extern int CreatePseudoConsole(COORD size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(IntPtr console,COORD size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")] static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string cwd, ref STARTUPINFOEX start, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
