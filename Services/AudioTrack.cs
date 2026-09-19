namespace CupriCut.Services;

/// <summary>
/// The track an export muxes in, as a file ffmpeg can open.
///
/// <para>A project carries its audio as a <c>data:</c> URI, and ffmpeg cannot read one of those.
/// So it is written to a temporary file for the length of the encode and deleted afterwards -
/// which is also why this is disposable rather than a plain path.</para>
///
/// <para><b>It knows what it must not be attached to.</b> A matte is a picture of an alpha channel
/// and a mask is the channel itself; neither is something anyone listens to, and both are usually
/// half of a pair whose other half already carries the sound. A PNG sequence has nowhere to put it
/// at all. Silently muxing audio into those would double the bytes of a keying pair to no purpose,
/// and quietly desynchronise anyone who assembled the two halves later.</para>
/// </summary>
public sealed class AudioTrack : IDisposable
{
    private readonly bool _temporary;

    private AudioTrack(string? path, string? source, bool temporary)
    {
        Path = path;
        Source = source;
        _temporary = temporary;
    }

    /// <summary>Where ffmpeg can read it, or null when there is nothing to mux.</summary>
    public string? Path { get; }

    /// <summary>What to call it in a report.</summary>
    public string? Source { get; }

    public bool Any => Path is { Length: > 0 };

    /// <summary>Nothing to mux.</summary>
    public static AudioTrack None { get; } = new(null, null, temporary: false);

    /// <summary>
    /// The track for this render, if any.
    /// </summary>
    /// <param name="cut">For resolving an explicitly named file against the composition roots.</param>
    /// <param name="composition">Whose project may be carrying one.</param>
    /// <param name="requested">A file named by the caller, which wins over the project's own.</param>
    /// <param name="wanted">False when the caller asked for no audio at all.</param>
    public static AudioTrack For(
        CupriCutService cut, Composition composition, string? requested, bool wanted)
    {
        if (!wanted) return None;

        // Named explicitly: read from the composition roots like any other input.
        if (requested is { Length: > 0 })
        {
            var path = cut.ResolveRead(requested);
            if (!File.Exists(path)) throw new CutPolicyException($"No audio file at '{requested}'.");
            return new AudioTrack(path, System.IO.Path.GetFileName(path), temporary: false);
        }

        // Otherwise whatever attach_audio put in the project - but only if the track came with it.
        // Cues without a track are enough to animate and not enough to mux, which is a difference
        // worth reporting rather than failing over.
        if (composition.Project?.Audio is not { Asset: { Length: > 0 } key } audio) return None;
        if (!composition.Project.Assets.TryGetValue(key, out var uri)) return None;

        var comma = uri.IndexOf(',');
        if (comma < 0 || !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return None;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(uri[(comma + 1)..]); }
        catch (FormatException) { return None; }

        var temporary = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"cupricut-{Guid.NewGuid():N}{System.IO.Path.GetExtension(key)}");

        File.WriteAllBytes(temporary, bytes);
        return new AudioTrack(temporary, audio.Source, temporary: true);
    }

    /// <summary>Whether this target is one that should carry sound.</summary>
    public static bool Suits(ExportTarget target) =>
        !target.IsSequence
        && target.Codec.AudioEncoder is { Length: > 0 }
        && target.AlphaMode is not (AlphaMode.MatteBelow or AlphaMode.MatteRight or AlphaMode.MaskOnly);

    public void Dispose()
    {
        if (!_temporary || Path is null) return;
        try { File.Delete(Path); } catch (IOException) { /* a temp file that outlives us is not worth failing a render over */ }
    }
}
