namespace CupriCut.Services;

/// <summary>Writes one rendered frame, raw RGBA, to wherever it is going.</summary>
internal delegate void FrameWriter(ReadOnlySpan<byte> frame);

/// <summary>Produces every frame of a clip, handing each to <paramref name="write"/> in order, and
/// returns how many there were. The two implementations are a sweep on this thread and a pool of
/// workers whose output is reordered before it arrives.</summary>
internal delegate int FrameSource(FrameWriter write, int frameBytes);

/// <summary>One file an export is asked to produce.</summary>
/// <param name="Name">What the caller asked for - the name that comes back in the answer.</param>
/// <param name="Codec">The encoder behind it.</param>
/// <param name="AlphaMode">How transparency is carried, if at all.</param>
/// <param name="Path">Where it lands.</param>
public sealed record ExportTarget(string Name, VideoCodec Codec, AlphaMode AlphaMode, string Path)
{
    /// <summary>Set for a target whose output is not one file, so the answer can say what it is
    /// instead of reporting the size of whichever file the pattern happened to name.</summary>
    public string? Note { get; init; }

    /// <summary>True when <see cref="Path"/> is an ffmpeg output PATTERN rather than a file.</summary>
    public bool IsSequence => Path.Contains("%0", StringComparison.Ordinal);

    /// <summary>How big what was written is. A sequence is the whole directory, since no one file
    /// is the answer.</summary>
    public long SizeOnDisk()
    {
        if (!IsSequence) return new FileInfo(Path) is { Exists: true } f ? f.Length : 0;

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (directory is null || !Directory.Exists(directory)) return 0;
        return new DirectoryInfo(directory).EnumerateFiles("*.png").Sum(f => f.Length);
    }
}


/// <summary>What an export decided to do: the render it needs, and the files it will make.</summary>
/// <param name="Alpha">Whether the render is transparent. ONE value for the whole export, because
/// the targets share the sweep.</param>
/// <param name="Targets">The files, in the order they were asked for.</param>
public sealed record ExportPlan(bool Alpha, IReadOnlyList<ExportTarget> Targets);

/// <summary>
/// The formats an export is offered in, named the way someone asks for them.
///
/// <para><b>Why a list and not a codec argument.</b> "h264 in an mp4 container at yuv420p" is a
/// true description of what someone wants and a useless way to ask for it. The names here are the
/// names of the OUTCOME - an mp4, a transparent webm, the mask - and each one carries the codec,
/// the pixel format and the alpha handling that outcome actually needs.</para>
/// </summary>
public static class ExportFormats
{
    /// <summary>PNG frames, written by ffmpeg from the same pipe as everything else rather than by
    /// a second code path. Kept out of <see cref="VideoEncoder.Codecs"/> deliberately: without a
    /// numbered pattern for an output, ffmpeg writes one file and overwrites it every frame.</summary>
    private static readonly VideoCodec PngSequence = new("png", "png", PixelFormat: null, ".png", Alpha: true, []);

    /// <summary>What each name produces. The alpha mode is the format's own requirement, not the
    /// caller's preference - a mask is the alpha channel by definition.</summary>
    private static readonly Dictionary<string, (string Codec, AlphaMode Mode, string What)> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["mp4"] = ("h264", AlphaMode.Embedded, "H.264 in an mp4. Plays everywhere; no alpha channel."),
            ["h265"] = ("h265", AlphaMode.Embedded, "H.265 in an mp4. Smaller than mp4, fussier about players."),
            ["webm"] = ("vp9", AlphaMode.Embedded, "VP9 in a webm. Carries a real alpha channel."),
            ["mov"] = ("prores", AlphaMode.Embedded, "ProRes 4444 in a mov. What an editor wants; large."),
            ["gif"] = ("gif", AlphaMode.Embedded, "An animated GIF, palette built from these frames."),
            ["mask"] = ("h264", AlphaMode.MaskOnly, "The alpha channel alone, black and white. Needs alpha."),
            ["matte"] = ("h264", AlphaMode.MatteBelow, "Colour on top, alpha below, in one opaque frame. Needs alpha."),
            ["frames"] = ("png", AlphaMode.Embedded, "A numbered PNG sequence."),
        };

    public static IEnumerable<string> Names => Known.Keys;

    /// <summary>
    /// Whether <paramref name="format"/> is made out of the alpha channel, so asking for it is
    /// asking for a transparent render.
    ///
    /// <para>Used to INFER alpha when the caller said nothing about it - "give me the mask" is a
    /// complete request, and making someone also say alpha:true is making them say the same thing
    /// twice. A caller who explicitly said alpha:false and then asked for a mask still gets the
    /// error, because that is a contradiction rather than an omission.</para>
    /// </summary>
    public static bool NeedsAlpha(string format) =>
        Known.TryGetValue(format, out var known) && known.Mode != AlphaMode.Embedded;

    /// <summary>Whether ANY of these formats is made out of the alpha channel.</summary>
    public static bool AnyNeedsAlpha(IEnumerable<string> formats) => formats.Any(NeedsAlpha);

    /// <summary>Every format and what it gives you, for a caller choosing between them.</summary>
    public static IEnumerable<object> Describe() =>
        Known.Select(k => new { format = k.Key, what = k.Value.What });

    /// <summary>The extension a format's files get, so a caller can predict the name.</summary>
    public static string Extension(string format) =>
        Known.TryGetValue(format, out var known)
            ? known.Codec == "png" ? ".png" : VideoEncoder.Codecs[known.Codec].Extension
            : throw Unknown(format);

    /// <summary>
    /// Turn a name into a target, resolving where it writes.
    /// </summary>
    /// <param name="format">One of <see cref="Names"/>.</param>
    /// <param name="alpha">Whether the render is transparent. A format that is nothing but alpha is
    /// refused without it rather than written as a white rectangle.</param>
    /// <param name="place">Given a file name, where it should be written.</param>
    public static ExportTarget Resolve(string format, bool alpha, Func<string, string> place)
    {
        if (!Known.TryGetValue(format, out var known)) throw Unknown(format);

        if (known.Mode != AlphaMode.Embedded && !alpha)
            throw new ArgumentException(
                $"'{format}' is made out of the alpha channel, and an opaque render has none - the file would be a flat rectangle. " +
                "Set alpha:true, or drop this format.", nameof(format));

        if (known.Codec == "png")
        {
            // Numbered rather than time-stamped, because ffmpeg's image2 muxer counts and cannot be
            // told the time. render_frames, which does the naming itself, still stamps the time.
            var pattern = place(Path.Combine(format, "frame_%05d.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(pattern)!);
            return new ExportTarget(format, PngSequence with { PixelFormat = alpha ? "rgba" : "rgb24" },
                AlphaMode.Embedded, pattern)
            {
                Note = $"A numbered PNG sequence in {Path.GetDirectoryName(pattern)}, not a single file.",
            };
        }

        var codec = VideoEncoder.Codecs[known.Codec];
        return new ExportTarget(format, codec, known.Mode, place(format + codec.Extension));
    }

    /// <summary>
    /// Decide the whole export: what alpha the render needs, and what files come out.
    ///
    /// <para><b>Why this is one function.</b> Three callers - the MCP tool, the CLI verb and the
    /// window - each worked this out for themselves, and each got it subtly differently. The CLI
    /// and the tool inferred alpha for the format table and then handed the RAW argument to the
    /// sweep, so an export of mp4 + mask rendered opaque: the mask's filter never engaged and the
    /// two files came out byte-identical. Nothing failed. The only way to notice was to look at
    /// the file sizes.</para>
    ///
    /// <para>So the decision lives here, once, and the alpha the targets were built against is the
    /// same value the caller must hand the sweep.</para>
    /// </summary>
    /// <param name="formats">The names asked for. Empty means mp4.</param>
    /// <param name="asked">What the caller said about alpha, if anything.</param>
    /// <param name="projectAlpha">What the project remembers.</param>
    /// <param name="place">Given a file name, where it should be written.</param>
    public static ExportPlan Plan(IReadOnlyList<string> formats, bool? asked, bool projectAlpha,
        Func<string, string> place)
    {
        var wanted = formats.Count > 0 ? formats : ["mp4"];

        // Asking for a mask IS asking for a transparent render, so it is inferred rather than
        // demanded twice. An explicit alpha:false alongside one still reaches Resolve and is
        // refused there - that is a contradiction, not an omission.
        var alpha = asked ?? (AnyNeedsAlpha(wanted) || projectAlpha);

        // Every name is resolved before anything renders, so a format nobody can spell, or one that
        // cannot work with this alpha, fails at once rather than after the sweep.
        return new ExportPlan(alpha, [.. wanted.Select(f => Resolve(f.Trim(), alpha, place))]);
    }

    private static ArgumentException Unknown(string format) =>
        new($"Unknown format '{format}'. Known formats: {string.Join(", ", Known.Keys)}.", nameof(format));
}
