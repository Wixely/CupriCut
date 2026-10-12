using System.Reflection;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// What an agent is told about this server before it calls anything.
///
/// <para><b>Why this is worth a test.</b> The instructions are sent once, at the initialization
/// handshake, and nothing in the product reads them back - so a wiring mistake is invisible from
/// every direction. The server starts, every tool works, and the consuming agent simply never
/// receives the one piece of guidance that changes its behaviour most. That is the same silent
/// shape as every other defect this repository has paid for.</para>
///
/// <para>The content assertions are deliberately few and deliberately about <i>meaning</i> rather
/// than wording: they pin the habits the instructions exist to establish, so the prose can be
/// improved without a test failing, and cannot be improved into saying nothing.</para>
/// </summary>
public class AgentInstructionTests
{
    private static string Instructions()
    {
        var program = typeof(CupriCut.Services.CupriCutService).Assembly.GetTypes()
            .Single(t => t.Name == "Program" && t.Namespace == "CupriCut");

        var field = program.GetField("AgentInstructions", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Program.AgentInstructions is gone.");

        return (string)field.GetRawConstantValue()!;
    }

    [Fact]
    public void They_are_actually_handed_to_the_mcp_server()
    {
        // Read out of the source, because the wiring is a lambda inside host construction and
        // there is no running server to ask. Same approach as VerbGuardTests, for the same reason:
        // the call site IS the definition, and a test that asserted only the constant would pass
        // happily while nothing sent it.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs"));

        Assert.Contains("ServerInstructions = AgentInstructions", source, StringComparison.Ordinal);
    }

    [Fact]
    public void They_establish_the_habit_the_whole_file_exists_for()
    {
        var text = Instructions();

        // Lint first, and lint always. If this stops being in here, the instructions have stopped
        // doing their job whatever else they say.
        Assert.Contains("lint", text, StringComparison.OrdinalIgnoreCase);

        // The premise that makes the habit necessary rather than fussy.
        Assert.Contains("silent", text, StringComparison.OrdinalIgnoreCase);

        // And the split that keeps an agent from deciding somebody's design for them.
        Assert.Contains("translate", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void They_stay_short_enough_to_be_worth_sending()
    {
        // These go into every client's context at the handshake, before anything has been asked
        // for. The protocol's own guidance is that they should help a model use the server and not
        // duplicate the tool descriptions, which is a length budget as much as a scope one. A
        // ceiling rather than a target: the cost of a long one is paid by every session.
        var text = Instructions();

        Assert.InRange(text.Length, 500, 4000);
    }

    [Fact]
    public void The_two_places_that_tell_an_agent_what_to_do_agree()
    {
        // AGENTS.md says the same five things to an agent working in the repository that the
        // handshake says to one driving the server. They are different audiences and the wording
        // differs, so this pins the SHAPE: both name the loop, and AGENTS.md points at the
        // constant so a reader of one finds the other.
        var agents = File.ReadAllText(Path.Combine(RepoRoot(), "AGENTS.md"));

        Assert.Contains("AgentInstructions", agents, StringComparison.Ordinal);
        Assert.Contains("lint", agents, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("translate", agents, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriCut.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
