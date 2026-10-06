using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>
/// Developer aid: with CLOUDDRIVE_SYNC_SNAPSHOTS=&lt;folder&gt; CloudDrive-Sync saves pictures of its own open windows there,
/// so the layout can be checked without taking pictures of the whole screen.
/// </summary>
internal static class WindowSnapshots
{
    public const string Variable = "CLOUDDRIVE_SYNC_SNAPSHOTS";

    public static void StartIfRequested(Application application)
    {
        var folder = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Save(application, folder), application.Dispatcher);
        timer.Start();
    }

    private static void Save(Application application, string folder)
    {
        foreach (Window window in application.Windows)
        {
            if (!window.IsVisible || window.Content is not FrameworkElement content || content.ActualWidth < 10) continue;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawRectangle(window.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White, null, bounds);
                    // The window area as it is - not stretched to the bounds of what is drawn.
                    var brush = new VisualBrush(content)
                    {
                        Stretch = Stretch.None,
                        AlignmentX = AlignmentX.Left,
                        AlignmentY = AlignmentY.Top,
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = bounds,
                    };
                    context.DrawRectangle(brush, null, bounds);
                }
                var bitmap = new RenderTargetBitmap((int)(bounds.Width * dpi.DpiScaleX), (int)(bounds.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var file = Path.Combine(folder, window.GetType().Name + ".png");
                using var stream = File.Create(file + ".tmp");
                encoder.Save(stream);
                stream.Close();
                File.Move(file + ".tmp", file, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The picture is being read; the next one follows in a second.
            }
        }
    }
}
