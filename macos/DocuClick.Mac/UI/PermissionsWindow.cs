using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DocuClick.Mac.Platform;

namespace DocuClick.Mac.UI;

/// <summary>
/// First-run / on-demand check of the three macOS permissions DocuClick
/// needs, with buttons that open the right System Settings pane. Polls every
/// second so a freshly granted permission shows up without reopening.
/// </summary>
internal sealed class PermissionsWindow : DialogWindow<bool>
{
    private readonly TextBlock[] _status = new TextBlock[3];
    private readonly Avalonia.Threading.DispatcherTimer _timer;

    public PermissionsWindow()
    {
        Title = "DocuClick – Berechtigungen";
        Width = 560;
        SizeToContent = SizeToContent.Height;

        var rows = new StackPanel { Spacing = 10 };
        var items = new[]
        {
            ("Bedienungshilfen", "Namen der angeklickten Elemente für die Beschreibungstexte."),
            ("Eingabeüberwachung", "Klicks (und nur die Enter-Taste) systemweit erkennen."),
            ("Bildschirmaufnahme", "Screenshots der angeklickten Fenster. Wirkt erst nach einem Neustart von DocuClick.")
        };
        for (var i = 0; i < items.Length; i++)
        {
            var index = i;
            _status[i] = new TextBlock { Width = 90, VerticalAlignment = VerticalAlignment.Center };
            var allow = Ui.Button("Erlauben …");
            allow.Click += (_, _) => Request(index);
            var text = new StackPanel { Children = { Ui.Section(items[i].Item1), Ui.Hint(items[i].Item2) } };
            var row = new DockPanel();
            DockPanel.SetDock(_status[i], Dock.Left);
            DockPanel.SetDock(allow, Dock.Right);
            allow.VerticalAlignment = VerticalAlignment.Center;
            row.Children.AddRange(new Control[] { _status[i], allow, text });
            rows.Children.Add(row);
        }

        var restart = Ui.Button("DocuClick neu starten");
        restart.Click += (_, _) => AppRestarter.Restart();
        var close = Ui.Button("Fertig", primary: true);
        close.Click += (_, _) => Complete(true);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                Ui.Hint("macOS fragt diese Freigaben einmalig ab. Nach „Erlauben …“ in den Systemeinstellungen den Schalter bei DocuClick einschalten."),
                Ui.Card(rows),
                Ui.ButtonRow(restart, close)
            }
        };

        _timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    public static bool AllGranted =>
        Native.dc_perm_accessibility(0) == 1 && Native.dc_perm_input_monitoring(0) == 1 && Native.dc_perm_screen_recording(0) == 1;

    private void Refresh()
    {
        var granted = new[] { Native.dc_perm_accessibility(0), Native.dc_perm_input_monitoring(0), Native.dc_perm_screen_recording(0) };
        for (var i = 0; i < 3; i++)
        {
            _status[i].Text = granted[i] == 1 ? "✅ erteilt" : "❌ fehlt";
        }
    }

    private static void Request(int which)
    {
        var alreadyAsked = which switch
        {
            0 => Native.dc_perm_accessibility(1),
            1 => Native.dc_perm_input_monitoring(1),
            _ => Native.dc_perm_screen_recording(1)
        };
        // The system prompt only appears once per app; afterwards the
        // switch has to be flipped in System Settings directly.
        if (alreadyAsked == 0)
        {
            Native.dc_open_privacy_pane(which);
        }
    }
}

internal static class AppRestarter
{
    public static void Restart()
    {
        // Relaunch the .app bundle (not the bare executable) so macOS
        // attributes the new process to the same app and its permissions.
        var executable = Environment.ProcessPath ?? "";
        var bundle = executable.Contains(".app/Contents/MacOS/") ? executable[..(executable.IndexOf(".app/", StringComparison.Ordinal) + 4)] : null;
        if (bundle is not null)
        {
            System.Diagnostics.Process.Start("/usr/bin/open", new[] { "-n", bundle });
        }

        Environment.Exit(0);
    }
}

