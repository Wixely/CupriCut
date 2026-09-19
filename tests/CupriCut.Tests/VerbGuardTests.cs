using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// The server refusing a CLI verb.
///
/// <para><b>Why this exists.</b> Two executables share a stem: <c>CupriCut</c> hosts the MCP server
/// and <c>CupriCut.Cli</c> takes verbs. Typing <c>CupriCut.exe lint promo.html</c> used to START THE
/// SERVER - the configuration binder treats an unrecognised positional as nothing at all, so the
/// verb vanished, the file was ignored, and the process sat there serving MCP. When a server was
/// already running it instead failed to bind a port, with an error about sockets that had nothing
/// to do with what was asked for.</para>
/// </summary>
public partial class VerbGuardTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("lint")]
    [InlineData("video")]
    [InlineData("export")]
    [InlineData("inspect")]
    [InlineData("footage")]
    public void A_cli_verb_is_refused_and_the_whole_command_is_handed_back(string verb)
    {
        var complaint = Refuse([verb, "promo.html", "--fps", "30"]);

        Assert.NotNull(complaint);
        Assert.Contains($"'{verb}' is a CLI verb", complaint);

        // The command is repeated in full, so the fix is a copy and paste rather than a retype.
        Assert.Contains($"{verb} promo.html --fps 30", complaint);
    }

    [Fact]
    public void A_word_that_is_not_a_verb_is_refused_too()
    {
        // A typo deserves an answer. There is no bare argument this program could have meant.
        var complaint = Refuse(["lnt", "promo.html"]);

        Assert.NotNull(complaint);
        Assert.Contains("'lnt' is not an option", complaint);
        Assert.Contains("cupricut", complaint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_options_the_server_actually_takes_are_left_alone()
    {
        Assert.Null(Refuse([]));
        Assert.Null(Refuse(["-c"]));
        Assert.Null(Refuse(["--console", "--software"]));
        Assert.Null(Refuse(["--Cut:EnableVideo=false"]));
        Assert.Null(Refuse(["/Server:Port=5999"]));
    }

    [Fact]
    public void The_verb_list_matches_the_one_the_CLI_dispatches()
    {
        // A list that drifts is worse than no list: it would name the CLI for some verbs and give
        // the generic message for others, which is exactly the confusing half of the original bug.
        // Read out of the CLI's own source, because that switch IS the definition - and scoped to
        // it, since a bare hunt for `"word" => Something` also finds the alpha-mode switch further
        // down the same file.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "cli", "Program.cs"));
        var block = DispatchBlock().Match(source);

        Assert.True(block.Success, "the CLI's verb switch could not be found in cli/Program.cs");
        var dispatched = Dispatch().Matches(block.Groups["body"].Value)
            .Select(m => m.Groups["verb"].Value).ToHashSet(StringComparer.Ordinal);

        var listed = Listed().ToHashSet(StringComparer.Ordinal);

        output.WriteLine($"cli dispatches : {string.Join(", ", dispatched.Order())}");
        output.WriteLine($"server lists   : {string.Join(", ", listed.Order())}");

        Assert.Equal(dispatched.Order(), listed.Order());
    }

    // ---- reaching the private check ------------------------------------------------------------

    private static string? Refuse(string[] args)
    {
        var program = typeof(CupriCut.Services.CupriCutService).Assembly.GetTypes()
            .Single(t => t.Name == "Program" && t.Namespace == "CupriCut");

        var method = program.GetMethod("Refuse", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Program.Refuse is gone - the guard has been removed.");

        return (string?)method.Invoke(null, [args]);
    }

    /// <summary>The verbs the server's message knows about, read off the array it declares.</summary>
    private static IEnumerable<string> Listed()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs"));
        var block = ListedBlock().Match(source);

        Assert.True(block.Success, "the server's verb list could not be found in Program.cs");
        return Quoted().Matches(block.Groups["body"].Value).Select(m => m.Groups["v"].Value);
    }

    [GeneratedRegex(@"return verb switch\s*\{(?<body>.*?)
\s*\};", RegexOptions.Singleline)]
    private static partial Regex DispatchBlock();

    [GeneratedRegex(@"""(?<verb>[a-z-]+)""\s*=>\s*[A-Z]")]
    private static partial Regex Dispatch();

    [GeneratedRegex(@"string\[\] verbs\s*=\s*\[(?<body>[^\]]*)\]", RegexOptions.Singleline)]
    private static partial Regex ListedBlock();

    [GeneratedRegex(@"""(?<v>[a-z-]+)""")]
    private static partial Regex Quoted();

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriCut.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
