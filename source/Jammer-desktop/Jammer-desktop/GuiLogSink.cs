using System;
using Avalonia.Threading;
using Serilog.Core;
using Serilog.Events;

namespace Jammer_desktop;

public sealed class GuiLogSink : ILogEventSink
{
    public static event Action? Updated;
    public static string Text { get; private set; } = "";
    public static event Action? InstallUpdated;
    public static string InstallText { get; private set; } = "";

    public void Emit(LogEvent logEvent)
    {
        bool install = logEvent.Properties.TryGetValue("SourceContext", out var source) && source.ToString() == "\"SshService\"";
        var line = $"{logEvent.Timestamp:HH:mm:ss} [{logEvent.Level}] {logEvent.RenderMessage()}";
        if (logEvent.Exception is not null) line += ": " + (AppLogging.Detailed ? logEvent.Exception.ToString() : logEvent.Exception.Message);
        Dispatcher.UIThread.Post(() =>
        {
            var text = (install ? InstallText : Text) + line + Environment.NewLine;
            if (text.Length > 65536)
            {
                int boundary = text.IndexOf('\n', text.Length - 65536);
                text = boundary < 0 ? text[^65536..] : text[(boundary + 1)..];
            }
            if (install) { InstallText = text; InstallUpdated?.Invoke(); }
            else { Text = text; Updated?.Invoke(); }
        });
    }
}
