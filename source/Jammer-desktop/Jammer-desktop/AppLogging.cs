using System.IO;
using Serilog;

namespace Jammer_desktop;

public static class AppLogging
{
    public static string Folder => Path.Combine(AppSettings.Folder, "Logs");

    public static void Configure()
    {
        Directory.CreateDirectory(Folder);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.Sink(new GuiLogSink())
            .WriteTo.Logger(logger => logger
                .Filter.ByIncludingOnly(_ => AppSettings.Current.LoggingEnabled)
                .WriteTo.File(Path.Combine(Folder, "log-.txt"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
                    shared: true))
            .CreateLogger();
        Log.Information("Jammer desktop started");
    }
}
