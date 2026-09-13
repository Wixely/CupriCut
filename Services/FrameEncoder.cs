using SkiaSharp;

namespace CupriCut.Services;

/// <summary>
/// PNG encoding, kept off the sweep.
///
/// <para>Measured on the engine: rendering a frame costs 5.6 ms and encoding it as a PNG costs
/// 45.6 ms - the encoder is the cost, not the renderer. So the sweep hands over a raster and moves
/// on, and the encodes finish on the thread pool. The video path never pays this at all: it pipes
/// raw RGBA straight into ffmpeg.</para>
/// </summary>
public static class FrameEncoder
{
    public const int DefaultQuality = 100;

    /// <summary>Encode now, on this thread. For the one-frame tools, where there is nothing to
    /// overlap with.</summary>
    public static byte[] Encode(SKImage image)
    {
        using var data = image.Encode(SKEncodedImageFormat.Png, DefaultQuality)
            ?? throw new InvalidOperationException("Skia could not encode the frame as a PNG.");
        return data.ToArray();
    }

    /// <summary>Take a copy of the sweep's surface snapshot. The sweep reuses one surface, so a
    /// frame that outlives the sink callback has to be a raster of its own.</summary>
    public static SKBitmap Copy(SKImage image)
    {
        var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (!image.ReadPixels(bitmap.PeekPixels(), 0, 0))
        {
            bitmap.Dispose();
            throw new InvalidOperationException("Could not read the frame's pixels back from the render surface.");
        }
        return bitmap;
    }

    /// <summary>Encode a batch to disk in parallel and return the paths in frame order. The caller
    /// owns the bitmaps and they are disposed here.</summary>
    public static string[] WriteAll(IReadOnlyList<(SKBitmap Bitmap, string Path)> frames)
    {
        var paths = new string[frames.Count];
        try
        {
            Parallel.For(0, frames.Count, i =>
            {
                var (bitmap, path) = frames[i];
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, DefaultQuality)
                    ?? throw new InvalidOperationException($"Skia could not encode frame {i} as a PNG.");
                using var stream = File.Create(path);
                data.SaveTo(stream);
                paths[i] = path;
            });
        }
        finally
        {
            foreach (var (bitmap, _) in frames) bitmap.Dispose();
        }
        return paths;
    }
}
