using System;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Jammer_desktop;

// Keep existing context loggers valid when the user changes the output directory.
internal sealed class LogFileSink : ILogEventSink, IDisposable
{
    private readonly object _gate = new();
    private Serilog.Core.Logger? _logger;
    private string? _folder;

    public void ChangeFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        lock (_gate)
        {
            if (_logger is not null && string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".jammer-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            var next = new LoggerConfiguration().MinimumLevel.Verbose()
                .WriteTo.File(Path.Combine(folder, "log-.txt"), rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true,
                    buffered: true, flushToDiskInterval: TimeSpan.FromSeconds(1)).CreateLogger();
            var previous = _logger;
            _logger = next;
            _folder = folder;
            previous?.Dispose();
        }
    }

    public void Emit(LogEvent logEvent) { lock (_gate) _logger?.Write(logEvent); }
    public void Dispose() { lock (_gate) { _logger?.Dispose(); _logger = null; _folder = null; } }
}
