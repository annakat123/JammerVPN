using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Jammer_desktop;
using Jammer_desktop.Views;
using Serilog;

static class LoggingCheck
{
    public static async Task Run()
    {
        string settings = Path.Combine(AppSettings.Folder, "settings.json");
        byte[]? saved = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var original = JsonSerializer.Serialize(AppSettings.Current);
        string root = Path.GetFullPath(Path.Combine("build", "logging-check-" + Guid.NewGuid().ToString("N")));
        string first = Path.Combine(root, "first"), second = Path.Combine(root, "second");
        try
        {
            AppSettings.Current.LogFolder = first;
            AppSettings.Current.LoggingEnabled = false;
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
            var view = new GeneralView();
            var toggle = view.FindControl<ToggleSwitch>("LoggingToggle")!;
            var context = Log.ForContext("ExistingContext", true);
            Log.Information("basic-connection-event");
            Log.Debug("hidden-basic-detail");
            Log.Error(new InvalidOperationException("basic-error"), "basic-error-event");
            Dispatcher.UIThread.RunJobs();
            Check(GuiLogSink.Text.Contains("basic-connection-event") && GuiLogSink.Text.Contains("basic-error-event") && !GuiLogSink.Text.Contains("hidden-basic-detail"), "basic GUI keeps connection events and errors, filters details");
            toggle.IsChecked = true;
            Check(AppLogging.Detailed && AppSettings.Current.LoggingEnabled, "General toggle enables details immediately");
            Log.Debug("visible-detailed-event");
            var type = typeof(VpnConnection).Assembly.GetType("Jammer_desktop.TunnelDiagnostics")!;
            var diagnostic = Activator.CreateInstance(type, "172.16.0.42")!;
            var fromTun = type.GetMethod("FromTun")!.CreateDelegate<Action<byte[]>>(diagnostic);
            byte[] packet = new byte[1500]; packet[0] = 0x45; packet[9] = 6;
            packet[12] = 172; packet[13] = 16; packet[15] = 42; packet[22] = 1; packet[23] = 187; packet[33] = 2;
            for (int i = 0; i < 1000; i++) fromTun(packet);
            Dispatcher.UIThread.RunJobs();
            Check(GuiLogSink.Text.Contains("visible-detailed-event") && GuiLogSink.Text.Split("VPN TCP443").Length - 1 == 48, "detailed logging keeps a bounded packet metadata sample");
            toggle.IsChecked = false;
            Log.Debug("hidden-after-disable");
            Log.Error("error-after-disable");
            toggle.IsChecked = true;
            AppLogging.ChangeFolder(second);
            context.Information("existing-context-after-folder-change");
            string blocked = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocked, "test");
            try { AppLogging.ChangeFolder(blocked); throw new Exception("Invalid folder accepted"); }
            catch (IOException) { }
            Check(AppLogging.Folder == second && AppSettings.Current.LogFolder == second, "failed folder change preserves the working destination");
            string host = Environment.GetEnvironmentVariable("DOTNET_ROOT") is string dotnetRoot ? Path.Combine(dotnetRoot, "dotnet.exe") : "dotnet";
            var start = new System.Diagnostics.ProcessStartInfo(host) { UseShellExecute = false };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--logging-reload"); start.ArgumentList.Add(second);
            using var child = System.Diagnostics.Process.Start(start)!;
            await child.WaitForExitAsync().ConfigureAwait(false); Check(child.ExitCode == 0, "saved folder and mode reload correctly");
            Log.CloseAndFlush();
            string a = string.Join("", Directory.GetFiles(first, "log-*.txt").Select(File.ReadAllText));
            string b = string.Join("", Directory.GetFiles(second, "log-*.txt").Select(File.ReadAllText));
            Check(a.Contains("basic-error") && a.Contains("basic-connection-event") && !a.Contains("hidden-basic-detail"), "basic events and exception details persist to file");
            Check(a.Contains("visible-detailed-event") && !a.Contains("hidden-after-disable") && a.Contains("error-after-disable"), "mode changes filter file output without losing errors");
            Check(b.Contains("existing-context-after-folder-change") && !a.Contains("existing-context-after-folder-change"), "existing context loggers follow the new folder without restarting");
        }
        finally
        {
            Log.CloseAndFlush();
            var restore = JsonSerializer.Deserialize<AppSettings>(original)!;
            AppSettings.Current.SelectedServer = restore.SelectedServer;
            AppSettings.Current.Language = restore.Language;
            AppSettings.Current.LoggingEnabled = restore.LoggingEnabled;
            AppSettings.Current.LogFolder = restore.LogFolder;
            if (saved is not null) File.WriteAllBytes(settings, saved); else if (File.Exists(settings)) File.Delete(settings);
        }
    }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); }
}
