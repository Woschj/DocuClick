using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DocuClick.Mac.Platform;

namespace DocuClick.Mac.UI;

/// <summary>Colors and small builders shared by all windows (same palette as the Windows Theme.xaml).</summary>
internal static class Ui
{
    public static readonly Color Accent = Color.Parse("#2D6CDF");
    public static readonly Color Recording = Color.Parse("#E63946");
    public static readonly IBrush WindowBackground = new SolidColorBrush(Color.Parse("#F3F3F6"));
    public static readonly IBrush CardBackground = Brushes.White;
    public static readonly IBrush CardBorder = new SolidColorBrush(Color.Parse("#E1E1E6"));
    public static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1F1F23"));
    public static readonly IBrush TextSecondary = new SolidColorBrush(Color.Parse("#3A3A3F"));
    public static readonly IBrush TextHint = new SolidColorBrush(Color.Parse("#8A8A92"));
    public static readonly IBrush HudBackground = new SolidColorBrush(Color.FromArgb(170, 25, 25, 28));
    public static readonly IBrush HudBorder = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));

    public static Border Card(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.AddRange(children);
        return new Border
        {
            Background = CardBackground,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    public static TextBlock Section(string text) => new()
    {
        Text = text, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = TextPrimary, Margin = new Thickness(0, 0, 0, 2)
    };

    public static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = TextSecondary
    };

    public static TextBlock Hint(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = TextHint, TextWrapping = TextWrapping.Wrap
    };

    public static TextBlock Error()
    {
        var error = Hint("");
        error.Foreground = new SolidColorBrush(Recording);
        error.IsVisible = false;
        return error;
    }

    public static void ShowError(TextBlock error, string message)
    {
        error.Text = message;
        error.IsVisible = true;
    }

    /// <summary>Read-only path box with a picker button on the right.</summary>
    public static DockPanel PathRow(TextBox box, Button browse)
    {
        var row = new DockPanel();
        browse.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(box);
        return row;
    }

    public static readonly Avalonia.Platform.Storage.FilePickerFileType AblaufFileType = new("DocuClick-Ablauf")
    {
        Patterns = new[] { "*.html", "*.canvas" }
    };

    public static Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        if (primary)
        {
            button.Classes.Add("accent");
        }

        return button;
    }

    public static StackPanel ButtonRow(params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.AddRange(buttons);
        return row;
    }
}

/// <summary>Native NSWindow access for Avalonia windows (positioning in screen points, overlay behavior).</summary>
internal static unsafe class NativeWindow
{
    public static nint Handle(Window window)
    {
        var platformHandle = window.TryGetPlatformHandle();
        if (platformHandle is null)
        {
            return 0;
        }

        // Avalonia.Native exposes the NSWindow via IMacOSTopLevelPlatformHandle;
        // read it by name so this doesn't depend on that internal-ish type.
        if (platformHandle.GetType().GetProperty("NSWindow")?.GetValue(platformHandle) is nint nsWindow && nsWindow != 0)
        {
            return nsWindow;
        }

        return platformHandle.Handle; // NSView — the native side resolves its window
    }

    /// <summary>Applies overlay behavior once the native window exists.</summary>
    public static void MakeOverlay(Window window, bool clickThrough, bool nonActivating)
    {
        void Apply()
        {
            var handle = Handle(window);
            if (handle != 0)
            {
                Native.dc_window_make_overlay(handle, clickThrough ? 1 : 0, nonActivating ? 1 : 0);
            }
        }

        window.Opened += (_, _) => Apply();
    }

    /// <summary>Frame in global top-left screen points, or null if not visible.</summary>
    public static Rect? Frame(Window window)
    {
        var handle = Handle(window);
        if (handle == 0)
        {
            return null;
        }

        var frame = stackalloc double[4];
        return Native.dc_window_frame(handle, frame) != 0 ? new Rect(frame[0], frame[1], frame[2], frame[3]) : null;
    }

    public static void SetTopLeft(Window window, double x, double y)
    {
        var handle = Handle(window);
        if (handle != 0)
        {
            Native.dc_window_set_top_left(handle, x, y);
        }
    }

    /// <summary>Main screen minus menu bar and Dock, in global top-left points.</summary>
    public static Rect MainScreenVisibleFrame()
    {
        var frame = stackalloc double[4];
        Native.dc_main_screen_visible_frame(frame);
        return new Rect(frame[0], frame[1], frame[2], frame[3]);
    }
}

/// <summary>
/// Base for DocuClick's modal-style dialogs. There is no main window to be
/// modal to, so a dialog is shown on its own and awaited. DocuClick is a
/// menu-bar app: it has to activate itself for the dialog to get keyboard
/// focus, and afterwards hands focus back to the app being recorded.
/// </summary>
internal abstract class DialogWindow<TResult> : Window
{
    private readonly TaskCompletionSource<TResult?> _result = new();

    protected DialogWindow()
    {
        Background = Ui.WindowBackground;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        Topmost = true;
        Closed += (_, _) => _result.TrySetResult(default);
    }

    public Task<TResult?> ShowAndWaitAsync()
    {
        var previousApp = Native.dc_frontmost_pid();
        Closed += (_, _) => Native.dc_activate_pid(previousApp);
        Native.dc_activate_self();
        Show();
        Activate();
        return _result.Task;
    }

    protected void Complete(TResult result)
    {
        _result.TrySetResult(result);
        Close();
    }
}

/// <summary>Simple message box (Avalonia has none built in).</summary>
internal sealed class MessageWindow : DialogWindow<bool>
{
    private MessageWindow(string message, bool isError)
    {
        Title = isError ? "DocuClick – Fehler" : "DocuClick";
        Width = 420;
        SizeToContent = SizeToContent.Height;

        var ok = Ui.Button("OK", primary: true);
        ok.IsDefault = true;
        ok.Click += (_, _) => Complete(true);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Ui.TextPrimary },
                Ui.ButtonRow(ok)
            }
        };
    }

    public static Task Show(string message, bool isError = false) => new MessageWindow(message, isError).ShowAndWaitAsync();
}
