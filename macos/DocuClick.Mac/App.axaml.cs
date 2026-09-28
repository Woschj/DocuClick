using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DocuClick.Services;

namespace DocuClick.Mac;

public partial class App : Application
{
    private AppController? _controller;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // A menu-bar app meant to keep running through a whole recording
        // session: a stray exception in a handler must not end it (logged
        // and swallowed on the UI thread, like the Windows app does).
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            LogService.Log($"Unbehandelte Ausnahme (UI-Thread): {e.Exception}");
            LogService.Flush();
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogService.Log($"Unbehandelte Ausnahme (Prozessende, IsTerminating={e.IsTerminating}): {e.ExceptionObject}");
            LogService.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogService.Log($"Unbeobachtete Task-Ausnahme: {e.Exception}");
            e.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // No main window: runs until "DocuClick beenden".
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _controller = new AppController(this);
            desktop.ShutdownRequested += (_, _) => _controller.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
