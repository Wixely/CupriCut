using System.Collections.Concurrent;
using SkiaSharp;

namespace CupriCut.Services;

/// <summary>
/// Writes a PNG sequence as the sweep produces it, in bounded memory.
///
/// <para><b>Why this exists.</b> The obvious implementation — collect every frame, then encode them
/// all in parallel — holds the entire run in RAM. At 1280x720 a frame is 3.7 MB, so a ten-minute
/// 120 fps sequence would want about 265 GB. That was survivable only because a frame ceiling
/// happened to cap the run at 1800; with long clips supported, it is the thing that breaks first.</para>
///
/// <para>So each frame is copied out of the sweep's surface, handed to the thread pool, and
/// forgotten. A semaphore bounds how many are in flight, which doubles as back-pressure: when the
/// encoders are saturated the sweep waits rather than running ahead and allocating. Peak memory is
/// the bound times the frame size — tens of megabytes — whatever the length of the clip.</para>
///
/// <para>The encode is still the expensive half (45 ms against the render's 5.6), so overlapping it
/// with the sweep is what keeps a sequence roughly render-bound rather than encoder-bound.</para>
/// </summary>
public sealed class FrameSequenceWriter : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<Exception> _failures = [];
    private readonly List<Task> _encodes = [];
    private readonly List<string> _paths = [];
    private bool _completed;

    /// <param name="maxInFlight">Frames that may be awaiting encode at once. Defaults to the
    /// processor count, which is also roughly how many encodes can actually progress.</param>
    public FrameSequenceWriter(int? maxInFlight = null)
    {
        MaxInFlight = Math.Max(2, maxInFlight ?? Environment.ProcessorCount);
        _slots = new SemaphoreSlim(MaxInFlight, MaxInFlight);
    }

    public int MaxInFlight { get; }

    /// <summary>Frames handed over so far.</summary>
    public int Count => _paths.Count;

    /// <summary>Copy a frame out of the sweep's surface and queue it for encoding. Blocks while the
    /// encoders are saturated, which is the back-pressure that bounds memory.</summary>
    public void Add(SKImage image, string path)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        // Surface a failed encode at the next frame rather than after rendering thousands more.
        ThrowIfFailed();

        _slots.Wait();
        SKBitmap bitmap;
        try
        {
            bitmap = FrameEncoder.Copy(image);
        }
        catch
        {
            _slots.Release();
            throw;
        }

        _paths.Add(path);
        _encodes.Add(Task.Run(() =>
        {
            try
            {
                using (bitmap)
                {
                    using var data = bitmap.Encode(SKEncodedImageFormat.Png, FrameEncoder.DefaultQuality)
                        ?? throw new InvalidOperationException($"Skia could not encode '{Path.GetFileName(path)}' as a PNG.");
                    using var stream = File.Create(path);
                    data.SaveTo(stream);
                }
            }
            catch (Exception ex)
            {
                _failures.Add(ex);
            }
            finally
            {
                _slots.Release();
            }
        }));
    }

    /// <summary>Wait for every queued encode and return the paths in frame order.</summary>
    public string[] Complete()
    {
        _completed = true;
        Task.WaitAll([.. _encodes]);
        ThrowIfFailed();
        return [.. _paths];
    }

    private void ThrowIfFailed()
    {
        if (_failures.IsEmpty) return;
        var failures = _failures.ToArray();
        throw new AggregateException(
            $"{failures.Length} frame(s) failed to encode; the first was: {failures[0].Message}", failures);
    }

    public void Dispose()
    {
        // A sweep that threw leaves encodes in flight holding bitmaps; let them finish rather than
        // race their disposal, then drop the semaphore.
        _completed = true;
        try { Task.WaitAll([.. _encodes]); }
        catch { /* Complete() reports failures; Dispose must not throw over them */ }
        _slots.Dispose();
    }
}
