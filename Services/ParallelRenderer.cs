using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CupriCut.Services;

/// <summary>What a sharded render cost, and how it was divided.</summary>
public sealed record ShardReport(int Frames, int Workers, double ElapsedMs)
{
    public double MsPerFrame => ElapsedMs / Math.Max(1, Frames);
}

/// <summary>
/// A long render, spread across cores, delivered in order.
///
/// <para><b>Why this is possible at all.</b> The sweep is sequential by necessity: a frame of an
/// impure composition is a function of the frames before it. A composition that is <b>pure in
/// <c>t</c></b> has no such dependency — measured byte-identical swept or direct — so its frames
/// are independent and can be rendered in any order by any number of documents. That is the whole
/// licence for this class, and <see cref="Purity"/> gates it.</para>
///
/// <para><b>Why the frames do not go to disk.</b> They did, at first, as numbered PNGs for ffmpeg
/// to read back — and it was <b>slower than rendering on one core</b>: 22.9s against 10.9s for a
/// 1200-frame clip. Of course it was. The first measurement this whole renderer was built on says
/// a frame costs 5.6 ms to render and 45 ms to encode as a PNG, so putting a PNG encode on every
/// frame swamps anything parallelism wins back, and then ffmpeg has to decode them all again. The
/// raw pipe was always the fast path; it just had to be fed from more than one thread.</para>
///
/// <para>So workers render into pooled buffers and a drain loop hands them to the consumer in
/// strict frame order. A bounded number of frames may be in flight, which caps memory at that many
/// times the frame size — tens of megabytes — however long the clip is, and doubles as
/// back-pressure when ffmpeg is the slower end.</para>
///
/// <para>Each worker opens its <b>own</b> document: <c>CupriDocument</c> is single-threaded, so
/// sharing one would be a data race rather than a speed-up.</para>
/// </summary>
public sealed class ParallelRenderer(CupriCutService cut, ILogger log)
{
    /// <summary>Below this many frames the setup costs more than the sharding saves.</summary>
    public const int WorthSharding = 48;

    /// <summary>
    /// How many workers to use when nobody says.
    ///
    /// <para><b>Not the processor count.</b> Measured on a 12-logical-core machine rendering 480
    /// frames at 1280x720:</para>
    ///
    /// <code>
    ///   1 worker   9.30 ms/frame     4 workers  2.23 ms/frame
    ///   2 workers  3.67 ms/frame     6 workers  1.79 ms/frame   &lt;- peak, 5.2x
    ///   3 workers  2.94 ms/frame     8 workers  1.80 ms/frame
    ///                               12 workers  5.75 ms/frame   &lt;- worse than 4
    /// </code>
    ///
    /// <para>It peaks around the PHYSICAL core count and then collapses: each worker carries its own
    /// document, surface and read-back bitmap, so past that point they are competing for cache and
    /// memory bandwidth rather than for arithmetic. Half the logical count is the closest portable
    /// guess at the physical one, and the cap keeps a very large machine from discovering the same
    /// cliff. <c>workers</c> overrides it for anyone who has measured their own.</para>
    /// </summary>
    public static int DefaultWorkers => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    /// <summary>
    /// Frames a worker may run ahead by, <b>counted per worker rather than shared</b>.
    ///
    /// <para>A shared budget deadlocks, and did. Workers are assigned frames round-robin, so when
    /// the fast ones take every slot for later frames, the worker holding the frame the drain is
    /// waiting for blocks on the budget and nothing can ever proceed: the drain waits for frame n,
    /// frame n's worker waits for a slot, and the slots are held by frames the drain cannot reach
    /// yet. A private budget per worker cannot starve anyone, because a worker only ever blocks on
    /// its own frames being consumed.</para>
    /// </summary>
    private const int BufferPerWorker = 2;

    /// <summary>Roughly how much memory the frames in flight may occupy. At 720p a frame is 3.7 MB
    /// and this changes nothing; at 4K it is 33 MB and the worker count comes down to suit, because
    /// twelve workers two frames ahead would be 800 MB of pixels.</summary>
    private const long InFlightBudgetBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Render every time in <paramref name="times"/> across <paramref name="workers"/> threads,
    /// invoking <paramref name="consume"/> once per frame, <b>in order</b>, on the calling thread.
    /// </summary>
    /// <param name="consume">Given the frame index and its RGBA bytes. The span is only valid for
    /// the duration of the call — the buffer is returned to the pool afterwards.</param>
    public ShardReport RenderInOrder(
        Composition composition,
        SweepSpec spec,
        IReadOnlyList<double> times,
        int workers,
        Action<int, ReadOnlySpan<byte>> consume,
        CancellationToken token = default)
    {
        var purity = Purity.Analyse(composition);
        if (!purity.PureInTime)
            throw new InvalidOperationException(
                "This composition is not pure in t, so its frames depend on the frames before them and cannot be " +
                $"rendered out of order. {purity.Summary}");

        workers = workers <= 0 ? DefaultWorkers : Math.Clamp(workers, 1, Environment.ProcessorCount);

        var options = cut.Options;
        var defaults = composition.Defaults;
        var width = spec.Width > 0 ? spec.Width : defaults?.Width > 0 ? defaults.Width : options.DefaultWidth;
        var height = spec.Height > 0 ? spec.Height : defaults?.Height > 0 ? defaults.Height : options.DefaultHeight;
        var scale = spec.Scale > 0 ? spec.Scale : defaults?.Scale > 0 ? defaults.Scale : 1;
        var alpha = spec.Alpha ?? defaults?.Alpha ?? false;
        var clear = alpha ? SKColors.Transparent : spec.Background ?? ParseColour(defaults?.Background) ?? SKColors.White;

        var pixelWidth = width * scale;
        var pixelHeight = height * scale;
        var frameBytes = pixelWidth * pixelHeight * 4;

        // Fewer, bigger frames rather than an unbounded appetite for memory.
        var affordable = (int)Math.Max(1, InFlightBudgetBytes / Math.Max(1, (long)frameBytes * BufferPerWorker));
        if (affordable < workers)
        {
            log.LogInformation(
                "Reducing to {Workers} render workers: {Bytes:N0} bytes a frame would not fit {Wanted} of them in flight",
                affordable, frameBytes, workers);
            workers = affordable;
        }

        var ready = new ConcurrentDictionary<int, byte[]>();
        var capacity = new SemaphoreSlim[workers];
        for (var i = 0; i < workers; i++) capacity[i] = new SemaphoreSlim(BufferPerWorker);
        var arrived = new SemaphoreSlim(0);
        var failures = new ConcurrentBag<Exception>();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(token);

        var sw = Stopwatch.StartNew();

        var producers = Task.Run(() => Parallel.For(0, workers,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            worker =>
            {
                try
                {
                    RenderShard(composition, times, worker, workers, width, height, scale, clear,
                        pixelWidth, pixelHeight, frameBytes, ready, capacity[worker], arrived, abort.Token);
                }
                catch (OperationCanceledException)
                {
                    // Another worker failed, or the caller gave up.
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    abort.Cancel();
                    arrived.Release(times.Count);   // unblock the drain so it can report the failure
                }
            }), CancellationToken.None);

        // Drain in order on this thread. A frame is handed over only once every earlier frame has
        // been, which is what lets the consumer be a pipe that cannot seek.
        try
        {
            for (var next = 0; next < times.Count; next++)
            {
                byte[]? buffer;
                while (!ready.TryRemove(next, out buffer))
                {
                    if (!failures.IsEmpty) throw Failure(failures);
                    abort.Token.ThrowIfCancellationRequested();
                    arrived.Wait(100, CancellationToken.None);
                }

                try
                {
                    consume(next, buffer.AsSpan(0, frameBytes));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    capacity[next % workers].Release();   // the worker that produced it
                }
            }
        }
        catch
        {
            abort.Cancel();
            throw;
        }
        finally
        {
            try { producers.GetAwaiter().GetResult(); }
            catch (Exception ex) { failures.Add(ex); }

            foreach (var (_, buffer) in ready) ArrayPool<byte>.Shared.Return(buffer);
            ready.Clear();
            foreach (var slot in capacity) slot.Dispose();
            arrived.Dispose();
        }

        if (!failures.IsEmpty) throw Failure(failures);

        sw.Stop();
        var report = new ShardReport(times.Count, workers, sw.Elapsed.TotalMilliseconds);
        log.LogInformation(
            "Rendered {Frames} frames of {Composition} on {Workers} workers in {Ms:0}ms ({Each:0.0} ms/frame)",
            report.Frames, Path.GetFileName(composition.Path), workers, report.ElapsedMs, report.MsPerFrame);
        return report;
    }

    private static AggregateException Failure(ConcurrentBag<Exception> failures) =>
        new($"{failures.Count} render worker(s) failed; the first was: {failures.First().Message}", failures);

    private void RenderShard(
        Composition composition, IReadOnlyList<double> times, int worker, int workers,
        int width, int height, int scale, SKColor clear,
        int pixelWidth, int pixelHeight, int frameBytes,
        ConcurrentDictionary<int, byte[]> ready, SemaphoreSlim capacity, SemaphoreSlim arrived,
        CancellationToken token)
    {
        using var document = cut.OpenDocument(composition);
        document.Animate(0);
        if (!document.Settle(width, height, TimeSpan.FromSeconds(cut.Options.SettleTimeoutSeconds)))
            throw new TimeoutException($"Worker {worker}: '{composition.Path}' still had resources loading after settling.");

        var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException($"Worker {worker}: could not create a {pixelWidth}x{pixelHeight} surface.");
        var canvas = surface.Canvas;

        // ffmpeg's rawvideo rgba is STRAIGHT alpha; Skia paints premultiplied. On an opaque render
        // the two are identical, so this costs nothing there.
        using var readback = new SKBitmap(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888,
            clear.Alpha == 0 ? SKAlphaType.Unpremul : SKAlphaType.Premul));

        // Interleaved, not blocked: a composition whose later frames are heavier would otherwise
        // leave one worker finishing alone while the rest idle.
        for (var i = worker; i < times.Count; i += workers)
        {
            token.ThrowIfCancellationRequested();
            capacity.Wait(token);

            document.Animate(times[i]);
            canvas.Clear(clear);
            canvas.Save();
            if (scale != 1) canvas.Scale(scale);
            document.Render(canvas, width, height);
            canvas.Restore();
            canvas.Flush();

            using (var image = surface.Snapshot())
            {
                if (!image.ReadPixels(readback.PeekPixels(), 0, 0))
                    throw new InvalidOperationException($"Worker {worker}: could not read frame {i} back.");
            }

            var buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
            readback.GetPixelSpan()[..frameBytes].CopyTo(buffer);
            ready[i] = buffer;
            arrived.Release();
        }
    }

    private static SKColor? ParseColour(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SKColor.TryParse(value, out var colour) ? colour : null;
}
