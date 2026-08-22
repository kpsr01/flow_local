using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingPoint = System.Drawing.Point;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace FlowLocal.App;

/// <summary>Renders the FlowLocal mark — an obsidian tile carrying an ember waveform.</summary>
public static class FlowIcon
{
    private static readonly double[] BarHeights = [0.34, 0.58, 0.82, 0.5, 0.3];

    public static ImageSource CreateImageSource(int pixelSize = 64)
    {
        var tile = new DrawingVisual();
        using (var context = tile.RenderOpen())
        {
            var side = (double)pixelSize;
            var cornerRadius = side * 0.235;
            var geometry = new RectangleGeometry(
                new Rect(0, 0, side, side), cornerRadius, cornerRadius);
            context.DrawGeometry(TileFill(), null, geometry);

            var brush = EmberGradient();
            foreach (var bar in WaveformBars(side))
            {
                var barRadius = bar.Width / 2;
                context.DrawRoundedRectangle(brush, null, bar, barRadius, barRadius);
            }
        }

        var bitmap = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(tile);
        bitmap.Freeze();
        return bitmap;
    }

    public static Icon CreateTrayIcon()
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create((BitmapSource)CreateImageSource(48)));
        encoder.Save(stream);
        stream.Position = 0;
        using var bitmap = new Bitmap(stream);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static MediaBrush TileFill()
    {
        var fill = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0, 1),
            GradientStops =
            {
                new GradientStop(MediaColor.FromRgb(0x21, 0x21, 0x28), 0),
                new GradientStop(MediaColor.FromRgb(0x14, 0x14, 0x19), 1)
            }
        };
        fill.Freeze();
        return fill;
    }

    private static MediaBrush EmberGradient()
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0, 1),
            GradientStops =
            {
                new GradientStop(MediaColor.FromRgb(0xFF, 0x8A, 0x55), 0),
                new GradientStop(MediaColor.FromRgb(0xFF, 0x5D, 0x33), 1)
            }
        };
        gradient.Freeze();
        return gradient;
    }

    private static Rect[] WaveformBars(double side)
    {
        const int barCount = 5;
        var barWidth = side * 0.095;
        var gap = side * 0.07;
        var rowWidth = barCount * barWidth + (barCount - 1) * gap;
        var left = (side - rowWidth) / 2;

        var bars = new Rect[barCount];
        for (var i = 0; i < barCount; i++)
        {
            var height = BarHeights[i] * side * 0.86;
            bars[i] = new Rect(left + i * (barWidth + gap), (side - height) / 2, barWidth, height);
        }
        return bars;
    }
}
