using System.Globalization;

namespace CupriCut.Services;

/// <summary>
/// A region of a frame someone marked, and what they said about it.
///
/// <para><b>Why this exists.</b> The loop is look, adjust, look — and until the window, only the
/// agent got to look. A reviewer had to open a PNG elsewhere and then describe the problem in
/// prose: "the logo comes in too late, and it's too far left". An annotation replaces the describing
/// with pointing. The agent gets a time, a rectangle and a sentence.</para>
///
/// <para><b>Coordinates are normalised 0–1 of the frame, never pixels.</b> A note drawn on a
/// 1280x720 preview has to still mean the same region when the project renders at 3840x2160, or at
/// whatever size the next person opens it. Pixels would silently point somewhere else.</para>
/// </summary>
public sealed class Annotation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The time on the composition's clock this was drawn at, in seconds.</summary>
    public double Time { get; set; }

    /// <summary>The frame number that time lands on, at the project's rate.
    ///
    /// <para>Kept alongside the time rather than derived, because deriving it needs the rate the
    /// project had WHEN THE NOTE WAS MADE - and an agent that changes the frame rate in response to
    /// the note would silently renumber every earlier one. The time is the truth; this is what the
    /// reviewer was looking at.</para></summary>
    public int Frame { get; set; }

    /// <summary>The rate the frame number was counted at, so it can be read back honestly.</summary>
    public double Fps { get; set; }

    /// <summary>Left edge, 0–1 of the frame width.</summary>
    public double X { get; set; }

    /// <summary>Top edge, 0–1 of the frame height.</summary>
    public double Y { get; set; }

    /// <summary>Width, 0–1 of the frame width.</summary>
    public double W { get; set; }

    /// <summary>Height, 0–1 of the frame height.</summary>
    public double H { get; set; }

    /// <summary>What the reviewer said is wrong, or wanted.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>Who left it — "reviewer" from the window, or an agent's own name.</summary>
    public string Author { get; set; } = "reviewer";

    public AnnotationStatus Status { get; set; } = AnnotationStatus.Open;

    /// <summary>What the agent did about it, set when it is resolved.</summary>
    public string? Resolution { get; set; }

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? Resolved { get; set; }

    /// <summary>The region in pixels at a given frame size — what an agent actually wants when it
    /// is about to look at the picture.</summary>
    public (int X, int Y, int W, int H) InPixels(int width, int height) =>
        ((int)Math.Round(X * width), (int)Math.Round(Y * height),
         (int)Math.Round(W * width), (int)Math.Round(H * height));

    /// <summary>A one-line reading, which is what a tool's answer should lead with.</summary>
    public string Describe(int width, int height)
    {
        var (px, py, pw, ph) = InPixels(width, height);
        var t = Time.ToString("0.###", CultureInfo.InvariantCulture);
        var frame = Fps > 0 ? $" (frame {Frame} at {Fps.ToString("0.##", CultureInfo.InvariantCulture)} fps)" : "";
        return $"[{Id}] t={t}s{frame}  {pw}x{ph} at ({px},{py}) of {width}x{height}  \"{Note}\"";
    }

    /// <summary>Stamp the frame number for a given rate. Called when the note is made.</summary>
    public Annotation AtRate(double fps)
    {
        if (fps <= 0) return this;
        Fps = fps;
        Frame = (int)Math.Round(Time * fps, MidpointRounding.AwayFromZero);
        return this;
    }

    /// <summary>Clamp to the frame and put the rectangle the right way round, so a drag that
    /// started at the bottom-right or ran off the edge still yields a sane region.</summary>
    public Annotation Normalised()
    {
        var x0 = Math.Clamp(Math.Min(X, X + W), 0, 1);
        var y0 = Math.Clamp(Math.Min(Y, Y + H), 0, 1);
        var x1 = Math.Clamp(Math.Max(X, X + W), 0, 1);
        var y1 = Math.Clamp(Math.Max(Y, Y + H), 0, 1);
        X = x0;
        Y = y0;
        W = x1 - x0;
        H = y1 - y0;
        return this;
    }
}

public enum AnnotationStatus
{
    Open,
    Resolved,
}
