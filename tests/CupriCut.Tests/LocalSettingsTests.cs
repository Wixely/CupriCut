using System.Text.Json;
using CupriCut.Configuration;
using CupriCut.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// Calibration writing what it measured, and that taking effect.
///
/// <para>Measuring the machine and then asking someone to hand-edit a config file is most of a
/// feature. The window's button used to set the worker count for the session and then print
/// "run cupricut calibrate --apply to make it permanent", which is not applying a measurement, it
/// is describing how to.</para>
/// </summary>
public sealed class LocalSettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "cupricut-local", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_measured_count_lands_under_the_Cut_section()
    {
        var path = LocalSettings.SaveRenderWorkers(_root, 8);

        Assert.Equal(Path.Combine(_root, "CupriCut.Local.json"), path);
        Assert.Equal(8, Read(path).GetProperty("Cut").GetProperty("RenderWorkers").GetInt32());
    }

    [Fact]
    public void Writing_it_twice_replaces_rather_than_repeats()
    {
        LocalSettings.SaveRenderWorkers(_root, 8);
        var path = LocalSettings.SaveRenderWorkers(_root, 4);

        Assert.Equal(4, Read(path).GetProperty("Cut").GetProperty("RenderWorkers").GetInt32());
    }

    [Fact]
    public void Everything_already_in_the_file_survives()
    {
        // Someone may well have put their own overrides here, and a measurement is no reason to
        // lose them.
        var path = LocalSettings.PathFor(_root);
        File.WriteAllText(path, """
            {
              "Cut": { "FfmpegPath": "D:/tools/ffmpeg.exe", "EnableVideo": true },
              "Serilog": { "MinimumLevel": "Debug" }
            }
            """);

        LocalSettings.SaveRenderWorkers(_root, 6);

        var cut = Read(path).GetProperty("Cut");
        Assert.Equal(6, cut.GetProperty("RenderWorkers").GetInt32());
        Assert.Equal("D:/tools/ffmpeg.exe", cut.GetProperty("FfmpegPath").GetString());
        Assert.True(cut.GetProperty("EnableVideo").GetBoolean());
        Assert.Equal("Debug", Read(path).GetProperty("Serilog").GetProperty("MinimumLevel").GetString());
    }

    [Fact]
    public void A_file_that_is_not_JSON_is_refused_rather_than_clobbered()
    {
        var path = LocalSettings.PathFor(_root);
        File.WriteAllText(path, "{ this was hand-written and is broken");

        var ex = Assert.Throws<CutPolicyException>(() => LocalSettings.SaveRenderWorkers(_root, 8));

        Assert.Contains("not valid JSON", ex.Message);
        // ...and it is still there.
        Assert.Contains("hand-written", File.ReadAllText(path));
    }

    [Fact]
    public void A_running_process_picks_the_change_up_without_a_restart()
    {
        // The claim the feature rests on. Every host loads CupriCut.Local.json with
        // reloadOnChange, so writing it is enough - there is no "now restart it" step, which is
        // what made the old button a suggestion rather than an action.
        LocalSettings.SaveRenderWorkers(_root, 2);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(_root)
            .AddJsonFile(LocalSettings.FileName, optional: true, reloadOnChange: true)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<CutOptions>().Bind(configuration.GetSection("Cut"));
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<CutOptions>>();

        Assert.Equal(2, monitor.CurrentValue.RenderWorkers);

        LocalSettings.SaveRenderWorkers(_root, 8);

        // The watcher is debounced and runs on a background thread, so this waits rather than
        // asserting into a race.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (monitor.CurrentValue.RenderWorkers != 8 && DateTime.UtcNow < deadline) Thread.Sleep(25);

        Assert.Equal(8, monitor.CurrentValue.RenderWorkers);
    }

    private static JsonElement Read(string path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
}
