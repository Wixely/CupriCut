namespace CupriCut.Configuration;

/// <summary>
/// What CupriCut is allowed to read, allowed to write, and allowed to spend.
///
/// <para>A GitHub server is read-only by default with repository allow-lists. A renderer's risk is
/// the mirror image of that: it <b>writes files</b>, and a request with a big enough frame count is
/// a full disk. So the analogue of read-only mode is the pair of roots below plus the two ceilings
/// - a tool may read compositions only from <see cref="CompositionRoots"/> and write only under
/// <see cref="OutputRoot"/>, and no single call may exceed <see cref="MaxFrames"/> swept frames or
/// <see cref="MaxPixels"/> pixels per frame.</para>
/// </summary>
public sealed class CutOptions
{
    public const string SectionName = "Cut";

    /// <summary>The only directories a tool may read compositions, stylesheets, images and fonts
    /// from. Relative entries resolve against the content root. A composition path that escapes
    /// every root - by <c>..</c>, by symlink, or by being absolute elsewhere - is refused.
    ///
    /// <para>Empty rather than defaulted, on purpose: the configuration binder APPENDS a JSON array
    /// onto a list that already has items, so a property default plus the shipped CupriCut.json
    /// would give every root twice. The fallback lives in
    /// <see cref="DefaultCompositionRoots"/> and is applied when this is empty.</para></summary>
    public List<string> CompositionRoots { get; set; } = [];

    /// <summary>Used when <see cref="CompositionRoots"/> is empty.</summary>
    public static readonly IReadOnlyList<string> DefaultCompositionRoots = ["compositions"];

    /// <summary>The only directory a tool may write under. Relative to the content root.</summary>
    public string OutputRoot { get; set; } = "output";

    /// <summary>The one directory that is both read and written: where <c>.cut.json</c> projects
    /// live. It is added to the composition roots automatically, so a project can be rendered by
    /// the tool that saved it.
    ///
    /// <para>Only files ending <c>.cut.json</c> may be written here. That restriction is what keeps
    /// a read-write root from becoming a general file drop - the write surface stays three named
    /// things: output PNGs and videos, and projects.</para></summary>
    public string ProjectRoot { get; set; } = "projects";

    /// <summary>Directories of font files registered into every document before it renders. The
    /// font policy is always <c>RegisteredOnly</c>, so this is what a composition falls back to
    /// when it declares no <c>@font-face</c> of its own. Empty falls back to
    /// <see cref="DefaultFontDirectories"/> - see the note on <see cref="CompositionRoots"/>.</summary>
    public List<string> FontDirectories { get; set; } = [];

    /// <summary>Used when <see cref="FontDirectories"/> is empty.</summary>
    public static readonly IReadOnlyList<string> DefaultFontDirectories = ["fonts"];

    /// <summary>ffmpeg executable. A bare name is looked up on PATH. The Docker image carries one;
    /// the release zips do not.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>False disables every video tool: PNG output only, ffmpeg never launched.</summary>
    public bool EnableVideo { get; set; } = true;

    /// <summary>
    /// Optional ceiling on the number of frames a single call may <b>sweep</b>. <b>Zero, the
    /// default, means no limit</b> - a renderer that cannot make a long clip is not a renderer.
    ///
    /// <para>It was 1800 at first, on the reasoning that a runaway request fills a disk. That was
    /// the wrong trade: a ten-minute title sequence is an ordinary thing to want, and the ceiling
    /// refused it. The real hazards are handled where they arise instead - a PNG sequence streams
    /// through <see cref="FrameSequenceWriter"/> in bounded memory rather than collecting the run,
    /// and <see cref="MaxPixels"/> still bounds a single frame, which is a different question from
    /// how many there are.</para>
    ///
    /// <para>Left here as an opt-in control for a shared instance, where bounding what one caller
    /// can spend is a legitimate thing to want.</para>
    /// </summary>
    public int MaxFrames { get; set; }

    /// <summary>Ceiling on pixels per rendered frame, after <c>scale</c> is applied. 8294400 is
    /// 3840x2160.</summary>
    public long MaxPixels { get; set; } = 8_294_400;

    /// <summary>Default frame size when a tool is called without one.</summary>
    public int DefaultWidth { get; set; } = 1280;

    /// <summary>Default frame height when a tool is called without one.</summary>
    public int DefaultHeight { get; set; } = 720;

    /// <summary>The rate the sweep steps at, and the default output frame rate. A frame is a
    /// function of <c>t</c> AND the frames before it, so this is also what "the frame at t" means:
    /// the last of a sweep taken at this many steps per second.</summary>
    public double DefaultFps { get; set; } = 30;

    /// <summary>How long <c>doc.Settle</c> may spend getting every image and font in before frame
    /// 0. A timeout is a failed render, not a warning - a frame with holes in it is wrong AND
    /// deterministic, which is worse than wrong and flaky.</summary>
    public int SettleTimeoutSeconds { get; set; } = 15;

    /// <summary>Above this many bytes a PNG is written under <see cref="OutputRoot"/> and its path
    /// returned instead of the image itself, rather than pushing megabytes of base64 through an
    /// agent's context.</summary>
    public int MaxInlineImageBytes { get; set; } = 4_000_000;

    /// <summary>
    /// Render threads for a parallel export. <b>Zero means the built-in guess</b>, which is half the
    /// logical core count.
    ///
    /// <para>Worth setting from <c>calibrate</c> rather than reasoning about: measured on a
    /// 12-logical-core machine the best count was 6 and the worst was 12, and the processor count
    /// is what a reasonable person would have picked. The right number depends on physical cores,
    /// cache and frame size, none of which are portably knowable.</para>
    /// </summary>
    public int RenderWorkers { get; set; }

    /// <summary>Seconds an ffmpeg encode may run before it is killed.</summary>
    public int FfmpegTimeoutSeconds { get; set; } = 600;
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5722;
    public string Path { get; set; } = "/mcp";

    /// <summary>Service name when running as a Windows Service.</summary>
    public string WindowsServiceName { get; set; } = "CupriCut";

    /// <summary>Optional MCP endpoint password. Blank disables MCP password auth.</summary>
    public string Password { get; set; } = string.Empty;
}
