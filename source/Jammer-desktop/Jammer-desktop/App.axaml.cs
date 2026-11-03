using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Jammer_desktop.ViewModels;
using Jammer_desktop.Views;
using System;
using Serilog;
using Avalonia.Threading;

namespace Jammer_desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        AppLogging.Configure();
        AppText.Apply(AppSettings.Current.Language);
        Dispatcher.UIThread.UnhandledException += (_, e) => Log.Error(e.Exception, "UI error");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled application error");
            Log.CloseAndFlush();
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => Log.CloseAndFlush();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
