using System;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Jammer_desktop;

public static class AppLogging
{
    private static readonly LoggingLevelSwitch Level = new();
    private static readonly LogFileSink FileSink = new();
    public static bool Detailed => Level.MinimumLevel == LogEventLevel.Debug;
    public static string Folder => string.IsNullOrWhiteSpace(AppSettings.Current.LogFolder)
        ? Path.Combine(AppSettings.Folder, "Logs") : AppSettings.Current.LogFolder;

    public static void SetDetailed(bool detailed) => Level.MinimumLevel = detailed ? LogEventLevel.Debug : LogEventLevel.Information;

    public static void ChangeFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        string previous = AppSettings.Current.LogFolder;
        FileSink.ChangeFolder(folder);
        AppSettings.Current.LogFolder = folder;
        try { AppSettings.Current.Save(); }
        catch { AppSettings.Current.LogFolder = previous; FileSink.ChangeFolder(Folder); throw; }
        Log.Information("Log folder changed to {Folder}", folder);
    }

    public static void Configure()
    {
        SetDetailed(AppSettings.Current.LoggingEnabled);
        Exception? folderError = null;
        try { FileSink.ChangeFolder(Folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppSettings.Current.LogFolder = "";
            FileSink.ChangeFolder(Folder);
            folderError = ex;
        }
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(Level)
            .WriteTo.Console()
            .WriteTo.Sink(new GuiLogSink())
            .WriteTo.Sink(FileSink)
            .CreateLogger();
        if (folderError is not null) Log.Warning(folderError, "Saved log folder unavailable; using the default folder");
        Log.Information("Jammer desktop started");
    }
}
