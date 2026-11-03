using System.Diagnostics;
using System.Reflection;
using Jammer_desktop;

static class ReadinessCheck
{
    public static async Task Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        var method = typeof(VpnConnection).Assembly.GetType("Jammer_desktop.TunnelReadiness")!
            .GetMethod("CheckAsync", BindingFlags.Static | BindingFlags.Public)!;
        Task Probe(CancellationToken stop) => (Task)method.Invoke(null, new object[] { "198.51.100.241", stop })!;

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try { await Probe(cancel.Token); throw new Exception("An unavailable address passed readiness"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        Console.WriteLine("PASS: unavailable Windows address waits and can be cancelled, instead of failing immediately with 10049");

        var elapsed = Stopwatch.StartNew();
        try { await Probe(CancellationToken.None); throw new Exception("An unavailable address passed readiness"); }
        catch (TimeoutException) when (elapsed.Elapsed >= TimeSpan.FromSeconds(7) && elapsed.Elapsed < TimeSpan.FromSeconds(12)) { }
        Console.WriteLine("PASS: readiness has a bounded timeout while the address remains unavailable");

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings")!;
        object?[] Before() => new[] { key.GetValue("ProxyEnable"), key.GetValue("ProxyServer"), key.GetValue("AutoConfigURL") };
        var before = Before();
        var report = typeof(VpnConnection).Assembly.GetType("Jammer_desktop.ProxyDiagnostics")!
            .GetMethod("ReportAsync", BindingFlags.Static | BindingFlags.Public)!;
        await (Task)report.Invoke(null, new object[] { CancellationToken.None })!;
        if (!before.SequenceEqual(Before())) throw new Exception("Proxy diagnostics changed Windows settings");
        Console.WriteLine("PASS: proxy diagnostics leave Windows proxy settings unchanged");
    }
}
