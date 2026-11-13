namespace SoakRunner;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h" or "help")
        {
            Console.Out.WriteLine(RunnerOptions.Usage);
            return 0;
        }
        RunnerOptions options;
        try
        {
            options = RunnerOptions.Parse(args);
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(RunnerOptions.Usage);
            return 2;
        }

        if (options.Mode == RunnerMode.Observe)
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT", options.Root);
        using var cancelled = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            return options.Mode == RunnerMode.Observe ? ObserverRunner.Run(options, cancelled.Token) : DriverRunner.Run(options);
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        catch (AdmissionDeniedException e)
        {
            Console.Error.WriteLine(e.Message);
            return 3;
        }
        catch (VersionMismatchException e)
        {
            Console.Error.WriteLine(e.Message);
            return 6;
        }
        catch (ProtocolException e)
        {
            Console.Error.WriteLine(e.Message);
            return 6;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.Error.WriteLine("SoakRunner: " + e.Message);
            return 4;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
