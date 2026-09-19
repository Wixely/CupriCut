using System.IO.Compression;
using System.Text;

namespace CupriCut.Services;

/// <summary>
/// A project as a container: a zip holding <c>project.json</c> and an <c>assets/</c> folder.
///
/// <para><b>Why, when <c>.cut.json</c> already works.</b> Because it stopped working at audio. A
/// project inlines its assets as <c>data:</c> URIs so that it is one file you can hand to another
/// machine, and that is right for a logo and absurd for four minutes of WAV: base64 adds 33% to
/// something that was already 40 MB, and the result is a JSON file no editor will open. The
/// alternatives were all bad - reference the audio by path and the project stops being one file, or
/// inline it under a size cap and the format silently changes shape at a threshold nobody can
/// see.</para>
///
/// <para>A container dissolves that rather than splitting it. The single-file property is exactly
/// preserved, the bytes are stored as bytes, and deflate makes the whole thing smaller than the
/// JSON was.</para>
///
/// <para><b>The manifest is the same schema.</b> <c>project.json</c> inside the zip is a
/// <see cref="CutProject"/> exactly as <c>.cut.json</c> is one, with asset values pointing at
/// entries instead of carrying payloads. Loading either produces the same object, so nothing
/// downstream - render, lint, the studio, annotations - knows or cares which it came from. The
/// container is transport, not a second model.</para>
///
/// <para><b>The extension decides, never a size.</b> <c>hero.cut.json</c> writes JSON;
/// <c>hero.cutpkg</c> writes a package. An automatic switch at some megabyte count would be the
/// same hidden threshold that made the size-cap option bad, arriving by a different route.</para>
/// </summary>
public static class CutPackage
{
    /// <summary>What makes a path a package.</summary>
    public const string Extension = ".cutpkg";

    /// <summary>The manifest, at the root of the zip.</summary>
    public const string Manifest = "project.json";

    /// <summary>Where payloads live.</summary>
    public const string AssetFolder = "assets/";

    public static bool IsPackagePath(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when these bytes begin with the zip signature. Used to read a file for what it
    /// IS rather than for what it is called, so a renamed project still opens.</summary>
    public static bool LooksLikePackage(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        return stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) == 4
               && magic[0] == 'P' && magic[1] == 'K' && magic[2] == 3 && magic[3] == 4;
    }

    /// <summary>Read a package into the same object a <c>.cut.json</c> parses to.</summary>
    public static CutProject Read(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);

        var manifest = zip.GetEntry(Manifest)
            ?? throw new CutPolicyException(
                $"'{name}' is a {Extension} with no {Manifest} in it, so there is nothing to open.");

        using var reader = new StreamReader(manifest.Open(), Encoding.UTF8);
        var project = CutProject.Parse(reader.ReadToEnd(), name);

        // Payloads come back as data: URIs, which is what every consumer already takes. The
        // container is undone at the door and nothing past this line behaves differently.
        foreach (var (key, value) in project.Assets.ToList())
        {
            if (!value.StartsWith(AssetFolder, StringComparison.Ordinal)) continue;

            var entry = zip.GetEntry(value)
                ?? throw new CutPolicyException(
                    $"'{name}' lists an asset '{key}' at '{value}', and there is no such entry in the package.");

            using var bytes = new MemoryStream();
            using (var payload = entry.Open()) payload.CopyTo(bytes);

            project.Assets[key] = $"data:{MediaTypeOf(value)};base64,{Convert.ToBase64String(bytes.ToArray())}";
        }

        return project;
    }

    /// <summary>Write a package. Assets carried as <c>data:</c> URIs are split back out into
    /// entries; anything else in the dictionary is left exactly as it is.</summary>
    public static void Write(string path, CutProject project)
    {
        // Built in memory first and moved into place, so an interrupted write cannot leave a
        // half-written zip where a project used to be.
        var temporary = path + ".writing";

        // A shallow copy: the caller's project must not come back with its assets rewritten as
        // paths, because it is very likely about to be used for something else.
        var manifest = project.CloneForPackaging();

        using (var file = File.Create(temporary))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var payloads = new List<(string Entry, byte[] Bytes)>();

            foreach (var (key, value) in project.Assets)
            {
                if (!TrySplit(value, out var mediaType, out var bytes)) continue;

                var entry = AssetFolder + Safe(key) + ExtensionFor(mediaType);
                payloads.Add((entry, bytes));
                manifest.Assets[key] = entry;
            }

            var json = zip.CreateEntry(Manifest, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(json.Open(), new UTF8Encoding(false)))
                writer.Write(manifest.ToJson());

            foreach (var (entry, bytes) in payloads)
            {
                // Already-compressed formats are stored rather than deflated: spending CPU to make
                // a PNG or an MP3 0.1% smaller is a cost with no benefit.
                var level = Incompressible(entry) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                using var stream = zip.CreateEntry(entry, level).Open();
                stream.Write(bytes);
            }
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Pull the media type and the bytes out of a <c>data:</c> URI.</summary>
    private static bool TrySplit(string value, out string mediaType, out byte[] bytes)
    {
        mediaType = "application/octet-stream";
        bytes = [];

        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;

        var comma = value.IndexOf(',');
        if (comma < 0) return false;

        var header = value[5..comma];
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return false;

        var declared = header[..^7];
        if (declared.Length > 0) mediaType = declared;

        try
        {
            bytes = Convert.FromBase64String(value[(comma + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            // Not decodable, so not a payload. Left in the manifest verbatim rather than dropped -
            // losing an asset silently would be worse than carrying something odd.
            return false;
        }
    }

    /// <summary>A key, made safe to be a file name. The key stays the truth; this is only how the
    /// entry is spelled, so a browsable zip does not depend on what someone named an asset.</summary>
    private static string Safe(string key)
    {
        var cleaned = new string([.. key.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]);

        return cleaned.Trim('.', '_') is { Length: > 0 } trimmed ? trimmed : "asset";
    }

    // The types a composition actually carries. An unknown one round-trips as .bin and
    // application/octet-stream, which is honest rather than a guess.
    private static readonly (string Media, string Ext)[] Types =
    [
        ("image/png", ".png"), ("image/jpeg", ".jpg"), ("image/gif", ".gif"),
        ("image/webp", ".webp"), ("image/svg+xml", ".svg"), ("image/avif", ".avif"),
        ("font/woff2", ".woff2"), ("font/woff", ".woff"), ("font/ttf", ".ttf"), ("font/otf", ".otf"),
        ("audio/mpeg", ".mp3"), ("audio/wav", ".wav"), ("audio/x-wav", ".wav"),
        ("audio/mp4", ".m4a"), ("audio/aac", ".aac"), ("audio/ogg", ".ogg"), ("audio/flac", ".flac"),
        ("video/mp4", ".mp4"), ("video/webm", ".webm"),
    ];

    private static string ExtensionFor(string mediaType) =>
        Types.FirstOrDefault(t => string.Equals(t.Media, mediaType, StringComparison.OrdinalIgnoreCase)).Ext ?? ".bin";

    private static string MediaTypeOf(string entry)
    {
        var ext = Path.GetExtension(entry);
        return Types.FirstOrDefault(t => string.Equals(t.Ext, ext, StringComparison.OrdinalIgnoreCase)).Media
               ?? "application/octet-stream";
    }

    private static bool Incompressible(string entry) =>
        Path.GetExtension(entry).ToLowerInvariant()
            is ".png" or ".jpg" or ".gif" or ".webp" or ".avif" or ".woff2" or ".woff"
            or ".mp3" or ".m4a" or ".aac" or ".ogg" or ".flac" or ".mp4" or ".webm";
}
