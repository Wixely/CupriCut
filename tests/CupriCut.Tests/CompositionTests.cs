using CupriCut.Services;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The two jobs the engine leaves to its host: finding a composition's stylesheet, and making its
/// relative sources mean what the author meant.
/// </summary>
public sealed class CompositionTests
{
    [Fact]
    public void A_linked_stylesheet_is_pulled_in_because_the_engine_ignores_link_tags()
    {
        using var harness = new Harness();
        File.WriteAllText(Path.Combine(harness.Compositions, "theme.css"), ".bar { background: #d9642a; }");
        var name = harness.WriteComposition("linked.html", """
            <link rel="stylesheet" href="theme.css">
            <div class="bar">x</div>
            """);

        var loaded = harness.Cut.LoadComposition(name);

        Assert.Contains("#d9642a", loaded.Css);
        Assert.DoesNotContain("<link", loaded.Html);
        Assert.Single(loaded.Stylesheets);
    }

    [Fact]
    public void A_relative_source_resolves_against_the_composition_not_the_working_directory()
    {
        // Without this, "logo.png" means a file next to wherever the server happened to be started.
        using var harness = new Harness();
        Directory.CreateDirectory(Path.Combine(harness.Compositions, "assets"));
        File.WriteAllBytes(Path.Combine(harness.Compositions, "assets", "logo.png"), [0x89, 0x50, 0x4E, 0x47]);
        var name = harness.WriteComposition("relative.html", """
            <cupri-image src="assets/logo.png"></cupri-image>
            """);

        var loaded = harness.Cut.LoadComposition(name);

        Assert.Contains("assets/logo.png", loaded.Html);
        Assert.Contains(harness.Compositions.Replace('\\', '/'), loaded.Html);
        Assert.DoesNotContain("src=\"assets/logo.png\"", loaded.Html);
    }

    [Fact]
    public void A_url_in_an_inline_style_block_resolves_too()
    {
        // @font-face above all: a composition that ships its own face writes url(...) here.
        using var harness = new Harness();
        var name = harness.WriteComposition("styled.html", """
            <div class="hero"></div>
            <style> .hero { background-image: url(pics/hero.jpg); } </style>
            """);

        var loaded = harness.Cut.LoadComposition(name);

        Assert.Contains(harness.Compositions.Replace('\\', '/') + "/pics/hero.jpg", loaded.Html);
    }

    [Theory]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("https://example.com/logo.png")]
    public void An_already_resolvable_source_is_left_alone(string source)
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("absolute.html", $"""<cupri-image src="{source}"></cupri-image>""");

        var loaded = harness.Cut.LoadComposition(name);

        Assert.Contains(source, loaded.Html);
    }

    [Fact]
    public void A_stylesheet_outside_the_composition_roots_is_refused()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("leaky.html", """
            <link rel="stylesheet" href="../../../secrets.css">
            """);

        Assert.Throws<CutPolicyException>(() => harness.Cut.LoadComposition(name));
    }

    [Fact]
    public void A_missing_composition_says_so()
    {
        using var harness = new Harness();
        Assert.Throws<FileNotFoundException>(() => harness.Cut.LoadComposition("nope.html"));
    }
}
