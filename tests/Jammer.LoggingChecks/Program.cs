using Jammer_desktop;
if (args.Contains("--logging-reload"))
{
    if (AppSettings.Current.LogFolder != args[^1] || !AppSettings.Current.LoggingEnabled) throw new Exception("Log preferences were not restored");
    Console.WriteLine("PASS: log folder and detailed mode survive a new process"); return;
}
if (args.Contains("--bench")) await LoggingBench.Run();
else await LoggingCheck.Run();
