using CupriFace.Diagnostics;

namespace CupriCut.Services;

/// <summary>
/// The engine's own reader, in the shape the rest of this codebase wants it: findings, at a given
/// frame, with an empty stylesheet rather than a null one.
///
/// <para>This used to be a gate. Through CupriFace 0.25.0 the engine kept ONE diagnostics sink for
/// the whole process and <c>CupriDoctor.Check</c> drained whatever was in it, so a check returned
/// findings produced by other documents on other threads - 62 of 200 clean documents reported a
/// warning belonging to another, and a thread doing nothing but RENDERING polluted 157 of 200
/// checks of an unrelated document. Everything here was serialised behind a lock, which fixed the
/// first case and could not fix the second.</para>
///
/// <para>Fixed in 0.25.1 (<see href="https://github.com/Wixely/CupriFace/issues/185">#185</see>),
/// and re-measured on the upgrade: 0 of 200 both ways, and 0 of 200 under a concurrent render. The
/// lock is gone. <c>InspectorTests</c> keeps the measurement as a canary, because the failure mode
/// is silent - a verdict looks like an answer either way.</para>
/// </summary>
public static class Doctor
{
    /// <summary>What the engine's own reader makes of a document, at a given frame.</summary>
    // Spelled out: `Finding` in this namespace is CupriCut's own.
    public static IReadOnlyList<CupriFace.Diagnostics.Finding> Check(
        string html, string css, int width, int height) =>
        [.. CupriDoctor.Check(html, css, width: width, height: height).Findings];
}
