using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Jammer.Core;
using Jammer_desktop;
using Serilog;

static class LoggingBench
{
    public static Task Run()
    {
        string previous = AppSettings.Current.LogFolder;
        bool mode = AppSettings.Current.LoggingEnabled;
        AppSettings.Current.LogFolder = Path.GetFullPath("build/logging-benchmark-output");
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var records = new List<object>();
        var type = typeof(VpnConnection).Assembly.GetType("Jammer_desktop.TunnelDiagnostics")!;
        byte[] packet = new byte[1500], key = new byte[32]; Random.Shared.NextBytes(packet); Random.Shared.NextBytes(key);
        packet[0] = 0x45; packet[6] = packet[7] = 0; packet[9] = 6;
        packet[12] = 172; packet[13] = 16; packet[14] = 0; packet[15] = 42;
        packet[22] = 1; packet[23] = 187;
        try
        {
            // Alternate order across pairs to reduce warm-up/order bias. Pair zero is warm-up.
            for (int pair = 0; pair < 5; pair++)
            foreach (bool detailed in pair % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                AppLogging.SetDetailed(detailed);
                var diagnostic = Activator.CreateInstance(type, "172.16.0.42")!;
                var outgoing = type.GetMethod("FromTun")!.CreateDelegate<Action<byte[]>>(diagnostic);
                var incoming = type.GetMethod("ToTun")!.CreateDelegate<Action<byte[]>>(diagnostic);
                var sent = type.GetMethod("FrameSent")!.CreateDelegate<Action>(diagnostic);
                var received = type.GetMethod("FrameReceived")!.CreateDelegate<Action>(diagnostic);
                using var process = Process.GetCurrentProcess();
                double cpu = process.TotalProcessorTime.TotalSeconds;
                long allocated = GC.GetTotalAllocatedBytes();
                var watch = Stopwatch.StartNew();
                byte[]? reply = null;
                const int count = 300000;
                for (int i = 0; i < count; i++)
                {
                    packet[33] = i % 1000 == 0 ? (byte)2 : (byte)16;
                    outgoing(packet); sent();
                    reply = Crypto.AES.Decrypt(Crypto.AES.Encrypt(packet, key), key);
                    received(); incoming(reply);
                }
                watch.Stop();
                process.Refresh();
                if (!reply!.SequenceEqual(packet)) throw new Exception("Benchmark data mismatch");
                var row = new { pair, detailed, packets = count, bytes = count * packet.Length, seconds = watch.Elapsed.TotalSeconds,
                    cpuSeconds = process.TotalProcessorTime.TotalSeconds - cpu, allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                    payloadMbps = count * packet.Length * 8d / watch.Elapsed.TotalSeconds / 1000000, measurement = "in-process AES and diagnostics; not internet bandwidth" };
                records.Add(row);
                Console.WriteLine("BENCH " + JsonSerializer.Serialize(row));
                Dispatcher.UIThread.RunJobs();
            }
            File.WriteAllText("build/logging-benchmark.json", JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Log.CloseAndFlush(); AppSettings.Current.LogFolder = previous; AppSettings.Current.LoggingEnabled = mode; AppLogging.SetDetailed(mode); }
        return Task.CompletedTask;
    }
}
