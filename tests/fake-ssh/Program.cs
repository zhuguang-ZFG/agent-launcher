// Fake SSH executable for testing: prints arguments and simulates remote agent execution
// Usage: fake-ssh.exe <args>
// Behavior:
//   - Prints all arguments to stdout
//   - If any arg contains "disconnect", exits with code 255 (SSH connection error)
//   - If any arg contains "hold=<seconds>", sleeps for that many seconds (long-running)
//   - Otherwise, sleeps for 2 seconds (simulating remote work) and exits with code 0

using System;

class Program
{
    static int Main(string[] args)
    {
        Console.WriteLine("FAKE-SSH: " + string.Join(" ", args));
        
        // 长驻进程（hold=<seconds>）
        var holdSeconds = 2;  // 默认 2 秒
        foreach (var arg in args)
        {
            if (arg.StartsWith("hold="))
            {
                if (int.TryParse(arg.Substring(5), out var seconds))
                    holdSeconds = seconds;
            }
        }
        
        // 检查是否需要模拟断连（但先等待 holdSeconds）
        var shouldDisconnect = false;
        foreach (var arg in args)
        {
            if (arg.Contains("disconnect"))
            {
                shouldDisconnect = true;
            }
        }
        
        Console.WriteLine($"FAKE-SSH: Holding for {holdSeconds} seconds");
        System.Threading.Thread.Sleep(holdSeconds * 1000);
        
        if (shouldDisconnect)
        {
            Console.WriteLine("FAKE-SSH: Simulating connection error after hold (exit 255)");
            return 255;
        }
        
        Console.WriteLine("FAKE-SSH: Remote agent completed");
        return 0;
    }
}
