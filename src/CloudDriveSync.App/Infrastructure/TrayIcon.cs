using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CloudDriveSync.App.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>
/// The symbol in the notification area: the CloudDrive-Sync icon with a coloured dot for the state (green up to date,
/// blue synchronising, yellow a decision is needed, red an error, grey paused) - like the dot of CloudDrives. A click
/// opens CloudDrive-Sync, the right mouse button shows a menu in the Windows 11 style, notifications appear as Windows
/// notifications.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly MenuItem _pauseItem;
    private readonly MenuItem _drivesItem;
    private readonly Action<bool> _pauseAll;
    private readonly Dictionary<Tone, Drawing.Icon> _icons = [];
    private Window? _anchor;
    private bool _allPaused;

    public TrayIcon(Action open, Action syncAll, Action<bool> pauseAll, Action exit)
    {
        _pauseAll = pauseAll;
        _icon = new Forms.NotifyIcon { Icon = IconFor(Tone.Neutral), Text = "CloudDrive-Sync", Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
        _icon.BalloonTipClicked += (_, _) => open();

        _menu = new ContextMenu();
        _menu.Items.Add(Item("CloudDrive-Sync öffnen", "\uE8A7", open, bold: true));
        _menu.Items.Add(Item("Alle jetzt synchronisieren", "\uE895", syncAll));
        _pauseItem = Item("Alle anhalten", "\uE769", () => _pauseAll(!_allPaused));
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(new Separator());
        _drivesItem = Item("CloudDrives (Laufwerke) öffnen", "\uEDA2", Companion.OpenDrivesProgram);
        _menu.Items.Add(_drivesItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Beenden", "\uE7E8", exit));
        _menu.Closed += (_, _) => _anchor?.Hide();
    }

    public void SetStatus(string text, Tone tone)
    {
        var tip = "CloudDrive-Sync – " + text;
        _icon.Text = tip.Length > 127 ? tip[..126] + "…" : tip;
        _icon.Icon = IconFor(tone);
    }

    public void SetAllPaused(bool allPaused)
    {
        _allPaused = allPaused;
        _pauseItem.Header = allPaused ? "Alle fortsetzen" : "Alle anhalten";
        _pauseItem.Icon = Glyph(allPaused ? "\uE768" : "\uE769");
    }

    public void Notify(string title, string text, bool warning) =>
        _icon.ShowBalloonTip(6000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    private void ShowMenu()
    {
        // CloudDrives may have been installed or removed in the meantime.
        _drivesItem.Visibility = Companion.DrivesProgram is null ? Visibility.Collapsed : Visibility.Visible;
        // WPF menus close when they lose the focus; a symbol in the notification area has none, so an invisible
        // window takes it while the menu is open.
        _anchor ??= new Window
        {
            Width = 1,
            Height = 1,
            Left = -10000,
            Top = -10000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
        };
        _anchor.Show();
        _anchor.Activate();
        _menu.PlacementTarget = _anchor;
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private static MenuItem Item(string header, string glyph, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header, Icon = Glyph(glyph), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        item.Click += (_, _) => action();
        return item;
    }

    private static TextBlock Glyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The icon with the dot of a state, made once per state in the size Windows uses there.</summary>
    private Drawing.Icon IconFor(Tone tone)
    {
        if (_icons.TryGetValue(tone, out var cached)) return cached;
        var size = Forms.SystemInformation.SmallIconSize.Width;
        using var bitmap = new Drawing.Bitmap(size, size);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/clouddrive-sync.ico"))!;
            using (var stream = resource.Stream)
            using (var app = new Drawing.Icon(stream, size, size))
                graphics.DrawIcon(app, new Drawing.Rectangle(0, 0, size, size));
            if (DotColour(tone) is { } colour)
            {
                var dot = Math.Max(6, (int)Math.Round(size * 0.5));
                using var ring = new Drawing.SolidBrush(Drawing.Color.White);
                using var fill = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml(colour));
                graphics.FillEllipse(ring, size - dot, size - dot, dot, dot);
                graphics.FillEllipse(fill, size - dot + 1, size - dot + 1, dot - 2, dot - 2);
            }
        }
        var handle = bitmap.GetHicon();
        try
        {
            // A copy owns its handle; the one from GetHicon is freed right away.
            using var temporary = Drawing.Icon.FromHandle(handle);
            cached = (Drawing.Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
        _icons[tone] = cached;
        return cached;
    }

    /// <summary>The same colours as the dot of CloudDrives; no dot while nothing is set up.</summary>
    private static string? DotColour(Tone tone) => tone switch
    {
        Tone.Ok => "#22C55E",
        Tone.Busy => "#3B82F6",
        Tone.Warning => "#F59E0B",
        Tone.Error => "#EF4444",
        Tone.Paused => "#9CA3AF",
        _ => null,
    };

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        _anchor?.Close();
    }
}
