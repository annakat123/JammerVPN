using Jammer.Deploy;
using Serilog;
using System.Net;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
if (args.Length == 2 && args[0] == "--probe")
{
    await ServerProbe.CheckAsync(IPAddress.Parse(args[1]));
    Console.WriteLine("PASS: actual encrypted TCP/ECDH/AES -> TUN -> forwarding/NAT -> DNS round trip");
}
else if (args.Length == 2 && args[0] == "--deploy")
{
    await SshService.DeploySelfHosted(args[1], 22, "root", Environment.GetEnvironmentVariable("JAMMER_SSH_PASSWORD")!);
    Console.WriteLine("PASS: installer and live DNS probe finished");
}
else
{
    foreach (var (address, port, user, password) in new[] {
        ("bad-ip",22,"root","x"), ("127.0.0.1",0,"root","x"),
        ("127.0.0.1",22,"","x"), ("127.0.0.1",22,"root","") })
    {
        try { await SshService.DeploySelfHosted(address, port, user, password); throw new Exception("Invalid arguments accepted"); }
        catch (ArgumentException) { }
    }
    foreach (var file in new[] {"setup-firewall.py", "install-server.sh"})
    {
        using var stream = typeof(SshService).Assembly.GetManifestResourceStream("Jammer.Deploy." + file);
        if (stream is null || stream.Length == 0) throw new Exception("Missing embedded resource " + file);
    }
    Console.WriteLine("PASS: invalid settings rejected; installer resources embedded");
}
Log.CloseAndFlush();
