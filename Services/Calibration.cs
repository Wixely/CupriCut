namespace CupriCut.Services;

/// <summary>One thing that either works on this machine or does not.</summary>
/// <param name="Group">What it belongs to, for grouping a table.</param>
/// <param name="Name">What was tried, in the words a caller would use.</param>
/// <param name="Ok">Whether it worked.</param>
/// <param name="Detail">What came back — the version string, the pixel format, the error.</param>
/// <param name="Fix">What to do instead, when it did not work.</param>
public sealed record CalibrationCheck(string Group, string Name, bool Ok, string Detail, string? Fix = null)
{
    public string Mark => Ok ? "OK" : "X";
}

/// <summary>What the whole run found.</summary>
public sealed record CalibrationReport(IReadOnlyList<CalibrationCheck> Checks, double ElapsedMs)
{
    public int Passed => Checks.Count(c => c.Ok);
    public int Failed => Checks.Count(c => !c.Ok);
    public bool AllPassed => Failed == 0;

    /// <summary>Only the failures — the default view, because a wall of ticks says nothing a
    /// single line could not.</summary>
    public IReadOnlyList<CalibrationCheck> Failures => [.. Checks.Where(c => !c.Ok)];

    /// <summary>A fixed-width table, for a console or a status strip.</summary>
    public string ToTable(bool errorsOnly = true)
    {
        var rows = errorsOnly ? Failures : Checks;
        if (rows.Count == 0)
            return $"All {Passed} checks passed in {ElapsedMs:0} ms.";

        var width = rows.Max(r => r.Name.Length);
        var lines = new List<string>();
        string? group = null;
        foreach (var row in rows)
        {
            if (row.Group != group)
            {
                group = row.Group;
                lines.Add(group);
            }
            lines.Add($"  [{(row.Ok ? "x" : " ")}] {row.Name.PadRight(width)}  {row.Detail}");
            if (!row.Ok && row.Fix is { Length: > 0 }) lines.Add($"      -> {row.Fix}");
        }

        lines.Add(errorsOnly
            ? $"{Failed} of {Checks.Count} checks failed ({ElapsedMs:0} ms). Pass all:true to see the rest."
            : $"{Passed} passed, {Failed} failed ({ElapsedMs:0} ms).");
        return string.Join(Environment.NewLine, lines);
    }
}
