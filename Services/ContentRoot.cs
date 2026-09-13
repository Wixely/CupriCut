namespace CupriCut.Services;

/// <summary>
/// Where CupriCut.json, compositions/, fonts/, projects/ and output/ live.
///
/// <para><b>One definition, deliberately.</b> This existed twice — once in <c>Program</c> and once
/// in <see cref="CupriCutService"/>'s constructor — and the copies disagreed the moment the app was
/// started as <c>dotnet CupriCut.dll</c> instead of through its own executable. Fixing one left the
/// other pointing at the SDK's install directory, which failed as
/// "Access to the path 'C:\Program Files\dotnet\output' is denied". Two computations of the same
/// thing is the bug; this is the fix.</para>
/// </summary>
public static class ContentRoot
{
    /// <summary>
    /// The process's own directory, except when the process is the shared host.
    ///
    /// <para><c>Environment.ProcessPath</c> is the right answer for a published app, and especially
    /// a single-file one, whose <c>AppContext.BaseDirectory</c> is a temporary extraction directory
    /// rather than anywhere the configuration sits. But under <c>dotnet CupriCut.dll</c> — which is
    /// what a VS Code launch and <c>dotnet run</c> both do — ProcessPath is <c>dotnet.exe</c> and
    /// its directory belongs to the SDK. There, the assembly's own directory is what is wanted.</para>
    /// </summary>
    public static string Locate()
    {
        var processPath = Environment.ProcessPath;

        var hostedByDotnet = processPath is null
            || string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);

        var root = hostedByDotnet
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;

        return Path.TrimEndingDirectorySeparator(root);
    }
}
