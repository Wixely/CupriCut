using System.IO.Compression;
using CupriCut.Services;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// The container format: a zip holding <c>project.json</c> and an <c>assets/</c> folder.
///
/// <para>The property that matters most is that NOTHING downstream can tell the difference. A
/// project loaded from a package has to be the same object as one loaded from JSON, payloads and
/// all, because every consumer - render, lint, the studio, annotations - was written against the
/// one shape and none of them should learn about the other.</para>
/// </summary>
public class CutPackageTests(ITestOutputHelper output)
{
    [Fact]
    public void A_project_survives_the_round_trip_with_its_assets_intact()
    {
        using var harness = new Harness();
        var original = WithAsset(out var bytes);

        harness.Cut.SaveProject("hero" + CutPackage.Extension, original);
        var loaded = harness.Cut.LoadProject("hero" + CutPackage.Extension);

        Assert.Equal(original.Html, loaded.Html);
        Assert.Equal(original.Css, loaded.Css);
        Assert.Equal(original.Render.Width, loaded.Render.Width);

        // The payload comes back as the data: URI every consumer already takes, byte for byte.
        var uri = loaded.Assets["logo"];
        Assert.StartsWith("data:image/png;base64,", uri);
        Assert.Equal(bytes, Convert.FromBase64String(uri["data:image/png;base64,".Length..]));
    }

    [Fact]
    public void Both_formats_load_to_the_same_thing()
    {
        // The whole design in one assertion: the container is transport, not a second model.
        using var harness = new Harness();
        var project = WithAsset(out _);

        harness.Cut.SaveProject("plain", project);
        harness.Cut.SaveProject("packed" + CutPackage.Extension, project);

        var fromJson = harness.Cut.LoadProject("plain");
        var fromPackage = harness.Cut.LoadProject("packed" + CutPackage.Extension);

        Assert.Equal(fromJson.Html, fromPackage.Html);
        Assert.Equal(fromJson.Assets["logo"], fromPackage.Assets["logo"]);
        Assert.Equal(fromJson.Annotations.Count, fromPackage.Annotations.Count);
    }

    [Fact]
    public void The_payload_is_stored_as_bytes_rather_than_base64()
    {
        // The reason this format exists. Base64 adds a third to something already large, and four
        // minutes of WAV inlined is a JSON file no editor will open.
        using var harness = new Harness();
        var project = new CutProject { Html = "<div class=\"a\"></div>" };

        var payload = new byte[200_000];
        new Random(4).NextBytes(payload);                       // incompressible, so a fair test
        project.Assets["track"] = "data:audio/wav;base64," + Convert.ToBase64String(payload);

        harness.Cut.SaveProject("plain", project);
        var packagePath = harness.Cut.SaveProject("packed" + CutPackage.Extension, project);
        var jsonPath = Path.Combine(harness.Root, "projects", "plain.cut.json");

        long json = new FileInfo(jsonPath).Length, package = new FileInfo(packagePath).Length;
        output.WriteLine($"json {json:N0} bytes, package {package:N0} bytes, payload {payload.Length:N0}");

        Assert.True(json > payload.Length * 1.3, "the JSON should carry the 33% base64 tax");
        Assert.True(package < payload.Length * 1.1, $"the package should be about the payload size, was {package:N0}");
    }

    [Fact]
    public void A_package_is_a_browsable_zip()
    {
        // Not an implementation detail: being able to open it and look is half of why a container
        // beats a blob. The manifest is at the root and the payloads are named after their keys.
        using var harness = new Harness();
        var path = harness.Cut.SaveProject("hero" + CutPackage.Extension, WithAsset(out _));

        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(e => e.FullName).Order().ToList();
        output.WriteLine(string.Join("\n", names));

        Assert.Contains(CutPackage.Manifest, names);
        Assert.Contains("assets/logo.png", names);
    }

    [Fact]
    public void The_manifest_is_a_project_with_paths_where_the_payloads_were()
    {
        using var harness = new Harness();
        var path = harness.Cut.SaveProject("hero" + CutPackage.Extension, WithAsset(out _));

        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry(CutPackage.Manifest)!.Open());
        var manifest = CutProject.Parse(reader.ReadToEnd(), "hero");

        Assert.Equal("assets/logo.png", manifest.Assets["logo"]);
        Assert.DoesNotContain("base64", reader.ReadToEnd());
    }

    [Fact]
    public void Packaging_does_not_rewrite_the_project_it_was_given()
    {
        // The caller's project is very likely about to be rendered. Handing it back with its
        // payloads replaced by paths would produce a composition full of missing images.
        using var harness = new Harness();
        var project = WithAsset(out _);

        harness.Cut.SaveProject("hero" + CutPackage.Extension, project);

        Assert.StartsWith("data:image/png;base64,", project.Assets["logo"]);
    }

    [Fact]
    public void A_renamed_package_still_opens()
    {
        // Sniffed by content, not trusted to its name. Someone will rename one.
        using var harness = new Harness();
        var packed = harness.Cut.SaveProject("hero" + CutPackage.Extension, WithAsset(out _));
        var renamed = Path.Combine(harness.Root, "projects", "renamed.cut.json");
        File.Move(packed, renamed);

        var loaded = harness.Cut.LoadProject("renamed");

        Assert.StartsWith("data:image/png;base64,", loaded.Assets["logo"]);
    }

    [Fact]
    public void An_asset_that_is_not_a_payload_is_carried_through_untouched()
    {
        // A URL, or anything else that is not a data: URI. Dropping it silently would lose an
        // asset; rewriting it would be a guess.
        using var harness = new Harness();
        var project = new CutProject { Html = "<div class=\"a\"></div>" };
        project.Assets["remote"] = "https://example.com/logo.png";

        harness.Cut.SaveProject("hero" + CutPackage.Extension, project);
        var loaded = harness.Cut.LoadProject("hero" + CutPackage.Extension);

        Assert.Equal("https://example.com/logo.png", loaded.Assets["remote"]);
    }

    [Fact]
    public void Both_formats_are_listed_as_projects()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("plain", new CutProject { Html = "<div></div>" });
        harness.Cut.SaveProject("packed" + CutPackage.Extension, new CutProject { Html = "<div></div>" });

        var listed = harness.Cut.ListProjects();

        Assert.Contains("plain.cut.json", listed);
        Assert.Contains("packed" + CutPackage.Extension, listed);
    }

    [Fact]
    public void A_package_with_no_manifest_says_so_rather_than_throwing_a_zip_error()
    {
        using var harness = new Harness();
        var path = Path.Combine(harness.Root, "projects", "broken" + CutPackage.Extension);

        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            zip.CreateEntry("something-else.txt");

        var ex = Assert.Throws<CutPolicyException>(() => harness.Cut.LoadProject("broken" + CutPackage.Extension));

        Assert.Contains(CutPackage.Manifest, ex.Message);
    }

    [Fact]
    public void A_rendered_package_produces_the_same_frame_as_the_json_it_came_from()
    {
        // The end of the argument. If a render cannot tell which file it was loaded from, the
        // container really is only transport.
        using var harness = new Harness();
        var project = new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 200, Height = 100 },
        };

        harness.Cut.SaveProject("plain", project);
        harness.Cut.SaveProject("packed" + CutPackage.Extension, project);

        Assert.Equal(Frame(harness, "plain.cut.json"), Frame(harness, "packed" + CutPackage.Extension));
    }

    private static byte[] Frame(Harness harness, string name)
    {
        var loaded = harness.Cut.LoadComposition(name);
        using var doc = harness.Cut.OpenDocument(loaded);
        doc.Animate(0);
        doc.Settle(200, 100, TimeSpan.FromSeconds(5));
        doc.Animate(1);

        using var image = doc.RenderToImage(200, 100);
        using var bitmap = SkiaSharp.SKBitmap.FromImage(image);
        return bitmap.GetPixelSpan().ToArray();
    }

    /// <summary>A project carrying one real PNG.</summary>
    private static CutProject WithAsset(out byte[] bytes)
    {
        bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEElEQVR4nGN4oKAARwzEcQDgQxIBzrTn9gAAAABJRU5ErkJggg==");

        var project = new CutProject
        {
            Html = "<div class=\"a\"></div>",
            Css = ".a{width:10px;height:10px;}",
            Render = new RenderSettings { Width = 640, Height = 360 },
        };

        project.Assets["logo"] = "data:image/png;base64," + Convert.ToBase64String(bytes);
        project.Annotations.Add(new Annotation { Note = "move the logo" });
        return project;
    }
}
