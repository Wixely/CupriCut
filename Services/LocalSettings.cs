using System.Text.Json;
using System.Text.Json.Nodes;

namespace CupriCut.Services;

/// <summary>
/// The per-machine configuration layer, written rather than hand-edited.
///
/// <para><b>Why <c>CupriCut.Local.json</c> and not <c>CupriCut.json</c>.</b> What gets written here
/// is a MEASUREMENT of this machine - the fastest worker count, and whatever else calibration
/// learns next. It is true of this box and false of the next one, so it does not belong in the file
/// that travels with the repository. The local file is already the documented per-machine override,
/// already last in the configuration order, and already ignored by git.</para>
///
/// <para><b>Why it takes effect immediately.</b> Every host loads this file with
/// <c>reloadOnChange: true</c>, so writing it is picked up by the running process through
/// <c>IOptionsMonitor</c> without a restart. Calibrating and applying is therefore one action with
/// no "now restart it" step and no editing a file by hand - which was the whole complaint.</para>
///
/// <para>Only the named key is touched. Anything else already in the file survives, because a
/// person may well have put something there.</para>
/// </summary>
public static class LocalSettings
{
    public const string FileName = "CupriCut.Local.json";

    /// <summary>Where the local overrides live for a given content root.</summary>
    public static string PathFor(string contentRoot) => Path.Combine(contentRoot, FileName);

    /// <summary>
    /// Write one key under the <c>Cut</c> section, and return the file it went to.
    /// </summary>
    /// <exception cref="CutPolicyException">The file could not be written - an installation under
    /// Program Files, a read-only mount. Said plainly, with the file named, because the fix is
    /// outside the program.</exception>
    public static string Set(string contentRoot, string key, JsonNode? value)
    {
        var path = PathFor(contentRoot);

        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? []
                : [];
        }
        catch (JsonException ex)
        {
            // Refuse rather than clobber: someone's hand-written overrides are in there.
            throw new CutPolicyException(
                $"'{path}' is not valid JSON ({ex.Message}), so it was left alone rather than overwritten. Fix or delete it and try again.");
        }

        if (root["Cut"] is not JsonObject section)
        {
            section = [];
            root["Cut"] = section;
        }
        section[key] = value;

        try
        {
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CutPolicyException(
                $"Could not write '{path}': {ex.Message}. Set Cut:{key} there by hand, or run from a directory you can write to.");
        }

        return path;
    }

    /// <summary>Persist the measured render parallelism. The one thing calibration has to be able
    /// to do for itself.</summary>
    public static string SaveRenderWorkers(string contentRoot, int workers) =>
        Set(contentRoot, nameof(Configuration.CutOptions.RenderWorkers), workers);
}
