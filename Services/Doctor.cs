using CupriFace.Diagnostics;

namespace CupriCut.Services;

/// <summary>
/// The engine's reader, one caller at a time - and a warning about what that does not buy.
///
/// <para><b>The engine keeps one diagnostics sink for the whole process.</b> It is not per-call and
/// not per-document. Anything that opens, settles or renders a document on any thread drops its
/// findings into it, and <see cref="CupriDoctor.Check"/> drains whatever is sitting there and
/// returns it as the answer for the document it was handed.</para>
///
/// <para>Measured twice, both times with two documents - one using a <c>letter-spacing</c> the
/// engine ignores and warns about, one with no such property anywhere in it:</para>
/// <list type="bullet">
/// <item><description>400 interleaved <c>Check</c> calls: 62 of 200 CLEAN documents reported the
/// other document's warning, and 110 of 200 that should have warned reported nothing. Never
/// duplicated - which is what says a finding MOVED rather than being copied. The same 400 run one
/// after another are exactly right, every time.</description></item>
/// <item><description>One thread doing nothing but opening and RENDERING the noisy document, never
/// calling the doctor at all, while another checked the clean one: <b>157 of 200</b> checks came
/// back carrying the renderer's warning.</description></item>
/// </list>
///
/// <para>The second number is the important one, and it is why this class is only half a fix.
/// Serialising every <c>Check</c> in the process stops CupriCut's own checks robbing each other,
/// and that is worth having. It does nothing about a concurrent render - and there is no lock that
/// would, because the studio holds a document open for as long as a preview is on screen. A
/// reader/writer lock with renders on the read side would block <c>lint</c> for as long as somebody
/// has the preview open, which is worse than the problem.</para>
///
/// <para>So: a <c>lint</c> answered while a render is in flight can carry a finding that belongs to
/// the render. It is silent either way - a verdict looks like an answer whether or not it is this
/// document's. It surfaced as a shipped composition failing its own lint-clean test for a property
/// it does not contain, while passing whenever it was run alone.</para>
///
/// <para>Raised as <see href="https://github.com/Wixely/CupriFace/issues/185">CupriFace#185</see>.
/// The real fix is upstream: a finding should stay with the document that produced it. This class
/// comes out when it does.</para>
///
/// <para><see cref="Gate"/> is public because the half-fix only works if EVERY check in the process
/// is behind it, tests included. Anything calling an overload this class does not wrap should take
/// the gate itself.</para>
/// </summary>
public static class Doctor
{
    /// <summary>Held for the duration of any <see cref="CupriDoctor"/> call in this process.</summary>
    public static readonly Lock Gate = new();

    /// <summary>What the engine's own reader makes of a document, at a given frame.</summary>
    public static IReadOnlyList<CupriFace.Diagnostics.Finding> Check(
        string html, string css, int width, int height)
    {
        lock (Gate)
            return [.. CupriDoctor.Check(html, css, width: width, height: height).Findings];
    }
}
