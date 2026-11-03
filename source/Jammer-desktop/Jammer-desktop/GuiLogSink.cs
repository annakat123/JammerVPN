using System;
using Avalonia.Threading;
using Serilog.Core;
using Serilog.Events;

namespace Jammer_desktop;

public sealed class GuiLogSink : ILogEventSink
{
    public static event Action? Updated;
    public static string Text { get; private set; } = "";

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out var source) && source.ToString() == "\"SshService\"") return;
        var line = $"{logEvent.Timestamp:HH:mm:ss} [{logEvent.Level}] {logEvent.RenderMessage()}";
        if (logEvent.Exception is not null) line += ": " + logEvent.Exception.Message;
        Dispatcher.UIThread.Post(() =>
        {
            Text += line + Environment.NewLine;
            if (Text.Length > 65536)
            {
                int boundary = Text.IndexOf('\n', Text.Length - 65536);
                Text = boundary < 0 ? Text[^65536..] : Text[(boundary + 1)..];
            }
            Updated?.Invoke();
        });
    }
}
