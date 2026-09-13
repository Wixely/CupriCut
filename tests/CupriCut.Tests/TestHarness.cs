using CupriCut.Configuration;
using CupriCut.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CupriCut.Tests;

/// <summary>A service pointed at a scratch directory, with the fonts the strict policy needs.
/// Disposing it takes the scratch directory with it.</summary>
public sealed class Harness : IDisposable
{
    public Harness(Action<CutOptions>? configure = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "cupricut-tests", Guid.NewGuid().ToString("N"));
        Compositions = Directory.CreateDirectory(Path.Combine(Root, "compositions")).FullName;
        Directory.CreateDirectory(Path.Combine(Root, "output"));
        Directory.CreateDirectory(Path.Combine(Root, "projects"));

        // The same faces the server ships, copied in so a test never depends on the machine.
        var fonts = Directory.CreateDirectory(Path.Combine(Root, "fonts")).FullName;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fonts"), "*.ttf"))
            File.Copy(file, Path.Combine(fonts, Path.GetFileName(file)), overwrite: true);

        Options = new CutOptions();
        configure?.Invoke(Options);

        Cut = new CupriCutService(new StaticOptions(Options), NullLogger<CupriCutService>.Instance) { ContentRoot = Root };
        Encoder = new VideoEncoder(Cut, NullLogger<VideoEncoder>.Instance);
    }

    public string Root { get; }
    public string Compositions { get; }
    public CutOptions Options { get; }
    public CupriCutService Cut { get; }
    public VideoEncoder Encoder { get; }

    /// <summary>Write a composition into the read-only root and return the name a tool would use.</summary>
    public string WriteComposition(string name, string html)
    {
        var path = Path.Combine(Compositions, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, html);
        return name;
    }

    /// <summary>A composition that animates a bar's width from 0 to 400 over two seconds, entirely
    /// with @keyframes - so it is pure in t and every frame is predictable from t alone.</summary>
    public const string Keyframed = """
        <div class="bar"></div>
        <style>
          body, html { font-family: "Noto Sans"; }
          .bar { height: 40px; background: #d9642a; animation: grow 2s linear both; }
          @keyframes grow { from { width: 0; } to { width: 400px; } }
        </style>
        """;

    /// <summary>A composition driven by a CSS transition, which interpolates from whatever the
    /// previous frame held - the case that makes sweeping the only honest reading of t.</summary>
    public const string Transitioned = """
        <div class="box"></div>
        <style>
          body, html { font-family: "Noto Sans"; }
          .box { width: 50px; height: 40px; background: #2a6ad9; transition: width 1s linear; animation: nudge 2s linear both; }
          @keyframes nudge { from { opacity: 1; } to { opacity: 0.99; } }
        </style>
        """;

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a scratch directory a virus scanner still has open is not a failure */ }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class StaticOptions(CutOptions value) : IOptionsMonitor<CutOptions>
    {
        public CutOptions CurrentValue { get; } = value;
        public CutOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CutOptions, string?> listener) => null;
    }
}
