using System;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// Rewrites Jellyfin Web's <c>index.html</c> to load the plugin's badge script.
/// </summary>
public static class WebTransformation
{
    /// <summary>
    /// The identifier that marks the injected script, and prevents injecting it twice.
    /// </summary>
    public const string ScriptElementId = "anime-dub-status-script";

    /// <summary>
    /// The name the badge script is served under, relative to Jellyfin Web's root.
    /// </summary>
    public const string ScriptFileName = "anime-dub-status.js";

    /// <summary>
    /// The name the badge data is served under, relative to Jellyfin Web's root.
    /// </summary>
    public const string DataFileName = "anime-dub-status.json";

    /// <summary>
    /// The name per-series episode coverage is served under, relative to Jellyfin Web's root.
    /// </summary>
    public const string TracksFileName = "anime-dub-status-tracks.json";

    private const string BodyCloseTag = "</body>";

    private const string ScriptTag =
        "<script id=\"" + ScriptElementId + "\" src=\"" + ScriptFileName + "\" defer></script>";

    /// <summary>
    /// Inserts the badge script tag into a Jellyfin Web HTML document.
    /// </summary>
    /// <param name="html">The original document.</param>
    /// <returns>
    /// The document with the script tag before its closing body tag, or the
    /// original document when it cannot be safely changed.
    /// </returns>
    public static string InjectScript(string html)
    {
        if (string.IsNullOrEmpty(html)
            || html.Contains(ScriptElementId, StringComparison.Ordinal))
        {
            return html;
        }

        var index = html.LastIndexOf(BodyCloseTag, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return html;
        }

        return string.Concat(html.AsSpan(0, index), ScriptTag, html.AsSpan(index));
    }
}
