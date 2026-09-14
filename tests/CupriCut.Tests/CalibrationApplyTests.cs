using CupriCut.Gui;
using CupriCut.Services;
using CupriFace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The window measuring the machine and then KEEPING the answer.
///
/// <para>Clicking "Use 8 workers" used to set it for the session and print "run
/// <c>cupricut calibrate --apply</c> to make it permanent". That is not a button that applies a
/// measurement, it is a button that tells you how to. This is the test that it writes.</para>
/// </summary>
public sealed class CalibrationApplyTests
{
    [Fact]
    public void Calibrating_in_the_window_and_applying_writes_it_where_the_next_run_will_read_it()
    {
        using var harness = new Harness();
        var model = new StudioModel();
        using var controller = new StudioController(
            harness.Cut, harness.Encoder, model, NullLogger<StudioController>.Instance);

        var app = new StudioApp(model) { FontSources = [.. FontFiles()] };
        using var doc = CupriDocument.Load(app.Html, app.Css).UseComponents(app.Components);
        foreach (var font in app.Fonts) doc.LoadFont(font);
        doc.Bind(app.Model!);
        controller.Attach(doc);

        // Navigate the way a person does: the Calibrate controls live on the Settings page, and
        // a hidden page's buttons are not somewhere a click can land.
        Activate(doc, app, "data-cut-view", "settings");
        Activate(doc, app, "data-cut-tab", "calibrate");
        Assert.Equal("settings", model.View);
        Assert.Equal("calibrate", model.SettingsTab);

        // Real calibration, on a worker, exactly as the button does it.
        Activate(doc, app, "data-cut-action", "calibrate");

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (model.Calibrating && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.False(model.Calibrating, "calibration did not finish: " + model.CalibrationSummary);

        // Measuring needs ffmpeg. Asserted rather than skipped: this repository's other tests
        // already depend on ffmpeg being here, and a silent skip is how a broken feature passes.
        Assert.True(model.RecommendedWorkers > 0,
            "no worker count was measured (is ffmpeg on PATH?): " + model.CalibrationSummary);

        var measured = model.RecommendedWorkers;
        Activate(doc, app, "data-cut-action", "apply-workers");

        // In the running process...
        Assert.Equal(measured, harness.Cut.Options.RenderWorkers);

        // ...and on disk, where the next run reads it.
        var path = LocalSettings.PathFor(harness.Root);
        Assert.True(File.Exists(path), $"nothing was written. {model.CalibrationSummary}");
        Assert.Contains($"\"RenderWorkers\": {measured}", File.ReadAllText(path));

        // And it says so, rather than telling someone to go and run a command.
        Assert.Contains("saved", model.CalibrationSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--apply", model.CalibrationSummary);
    }

    /// <summary>
    /// Click the element carrying an attribute, the way the window does.
    ///
    /// <para><b>Re-bind first.</b> Rendering does not re-read the model - the desktop host re-binds
    /// on its own timer (<c>RefreshIntervalSeconds</c>), which is why a live window keeps up. Without
    /// that here, a button revealed by a model change is still laid out at zero size, the click
    /// lands on whatever is behind it, and the test fails somewhere else entirely: this one silently
    /// ran calibration a second time instead of applying it.</para>
    /// </summary>
    private static void Activate(CupriDocument doc, StudioApp app, string attribute, string value)
    {
        var box = Layout(doc, app, attribute, value)
            ?? throw new InvalidOperationException($"no element with {attribute}=\"{value}\"");

        if (box.Width <= 0 || box.Height <= 0)
            throw new InvalidOperationException(
                $"{attribute}=\"{value}\" laid out at {box.Width}x{box.Height}, so a click cannot land on it.");

        doc.DispatchClick(box.MidX, box.MidY);
    }

    private static SkiaSharp.SKRect? Layout(CupriDocument doc, StudioApp app, string attribute, string value)
    {
        doc.Bind(app.Model!);
        using (doc.RenderToImage(app.Width, app.Height)) { }
        return Locate(doc.Root, 0, 0, attribute, value);
    }

    private static SkiaSharp.SKRect? Locate(CupriFace.Dom.RenderNode node, float ox, float oy, string attribute, string value)
    {
        var x = ox + node.X;
        var y = oy + node.Y;
        if (node.Element?.GetAttribute(attribute) == value)
            return new SkiaSharp.SKRect(x, y, x + node.Width, y + node.Height);
        foreach (var child in node.Children)
            if (Locate(child, x, y, attribute, value) is { } hit) return hit;
        return null;
    }

    private static IEnumerable<CupriFace.Resources.CupriSource> FontFiles() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fonts"), "*.ttf")
            .Order(StringComparer.Ordinal)
            .Select(CupriFace.Resources.CupriSource.File);
}
