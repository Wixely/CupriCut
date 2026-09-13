using SkiaSharp;

namespace CupriCut.Services;

/// <summary>
/// N frames tiled into one image, each stamped with the time it was taken at.
///
/// <para>The single highest-value thing here. An agent's loop is look, adjust, look, and a whole
/// motion in one picture is what makes the "look" step cost one image instead of thirty. The
/// timestamps are the part that makes it actionable: "the logo is still off-screen at 0.80s" is a
/// note an agent can act on, and a strip of unlabelled thumbnails is not.</para>
/// </summary>
public static class ContactSheet
{
    private const int Gap = 8;
    private const int LabelHeight = 22;
    private static readonly SKColor Paper = new(0x1E, 0x1E, 0x1E);
    private static readonly SKColor Ink = new(0xE8, 0xE8, 0xE8);
    private static readonly SKColor Checker = new(0x2A, 0x2A, 0x2A);

    /// <summary>Tile <paramref name="cells"/> into a grid of <paramref name="columns"/>.
    /// The cells are disposed.</summary>
    /// <param name="alpha">Draw a checkerboard behind each cell, so a transparent composition reads
    /// as transparent rather than as black.</param>
    public static byte[] Compose(IReadOnlyList<(SKBitmap Frame, double Time)> cells, int columns, bool alpha)
    {
        if (cells.Count == 0) throw new ArgumentException("A contact sheet needs at least one frame.", nameof(cells));
        try
        {
            columns = Math.Clamp(columns, 1, cells.Count);
            var rows = (int)Math.Ceiling(cells.Count / (double)columns);
            var cellW = cells.Max(c => c.Frame.Width);
            var cellH = cells.Max(c => c.Frame.Height);

            var width = Gap + columns * (cellW + Gap);
            var height = Gap + rows * (cellH + LabelHeight + Gap);

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info)
                ?? throw new InvalidOperationException($"Could not create a {width}x{height} contact sheet surface.");
            var canvas = surface.Canvas;
            canvas.Clear(Paper);

            // The sheet's own furniture, not the composition's: a platform face is the right choice
            // for a caption, and it is why the sheet is a preview rather than an output frame.
            using var font = new SKFont(SKTypeface.Default, 13);
            using var ink = new SKPaint { Color = Ink, IsAntialias = true };
            using var checkerPaint = new SKPaint { Color = Checker };

            for (var i = 0; i < cells.Count; i++)
            {
                var (frame, time) = cells[i];
                var col = i % columns;
                var row = i / columns;
                var x = Gap + col * (cellW + Gap);
                var y = Gap + row * (cellH + LabelHeight + Gap);

                if (alpha) DrawChecker(canvas, checkerPaint, x, y, cellW, cellH);
                canvas.DrawBitmap(frame, x, y);

                var label = $"t = {time:0.###}s";
                canvas.DrawText(label, x, y + cellH + LabelHeight - 7, SKTextAlign.Left, font, ink);
            }

            canvas.Flush();
            using var image = surface.Snapshot();
            return FrameEncoder.Encode(image);
        }
        finally
        {
            foreach (var (frame, _) in cells) frame.Dispose();
        }
    }

    private static void DrawChecker(SKCanvas canvas, SKPaint paint, int x, int y, int w, int h)
    {
        const int Square = 12;
        canvas.Save();
        canvas.ClipRect(SKRect.Create(x, y, w, h));
        for (var cy = 0; cy < h; cy += Square)
        {
            for (var cx = (cy / Square % 2) * Square; cx < w; cx += Square * 2)
            {
                canvas.DrawRect(SKRect.Create(x + cx, y + cy, Square, Square), paint);
            }
        }
        canvas.Restore();
    }
}
