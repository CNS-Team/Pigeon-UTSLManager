using System.Text;
Console.InputEncoding = Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"\u001b[36mCONPTY_READY PID={Environment.ProcessId} SIZE={Console.WindowWidth}x{Console.WindowHeight} 中文控制台\u001b[0m");
string? line;
while ((line = Console.ReadLine()) != null) {
    if (line.Trim() == "/off") { Console.WriteLine("SAVED_AND_STOPPED"); break; }
    if (line.Trim() == "colors") Console.WriteLine("\u001b[31mRED\u001b[0m \u001b[32mGREEN\u001b[0m 中文回显");
    else Console.WriteLine("ECHO:" + line);
}
