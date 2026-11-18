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
        // 维护通道夹具：任一参数（如主机地址）含标记时输出固定内容并立即退出（不打印 FAKE-SSH 前缀，
        // 保证 --version 首行/资源输出可被真实解析器消费）。标记互不为子串，既有 disconnect/hold 行为不变。
        foreach (var arg in args)
        {
            if (arg.Contains("resources-ok"))
            {
                Console.WriteLine("               total        used        free      shared  buff/cache   available");
                Console.WriteLine("Mem:     17179869184  4294967296  2147483648   1048576 10737418240 12884901888");
                Console.WriteLine("Swap:              0           0           0");
                Console.WriteLine("4");
                Console.WriteLine("0.52 0.58 0.59 1/234 5678");
                return 0;
            }
            if (arg.Contains("resources-bad")) { Console.WriteLine("Mem: not-a-number garbage"); return 0; }
            if (arg.Contains("help-ok"))
            {
                Console.WriteLine("Usage: pi [options]");
                Console.WriteLine("  -c, --continue       Continue the last session in this directory");
                Console.WriteLine("  -r, --resume [id]    Resume a session");
                return 0;
            }
            if (arg.Contains("help-missing"))
            {
                Console.WriteLine("Usage: pi [options]");
                Console.WriteLine("  --model <name>       Model to use");
                return 0;
            }
            if (arg.Contains("version-ok")) { Console.WriteLine("1.2.3"); return 0; }
        }

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
