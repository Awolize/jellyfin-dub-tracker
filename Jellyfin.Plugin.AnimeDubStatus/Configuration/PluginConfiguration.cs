using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AnimeDubStatus.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        DataETag = string.Empty;
        MappingETag = string.Empty;
        Source = TrackSourceCatalog.MyDubList;
        Language = TrackLanguageCatalog.Default;
        TrackKind = TrackSettings.Dub;
        ConfidenceTier = string.Empty;
        DatasetKey = string.Empty;
    }

    /// <summary>
    /// Gets or sets the identifier of the data source to read.
    /// </summary>
    public string Source { get; set; }

    /// <summary>
    /// Gets or sets the code of the language to track.
    /// </summary>
    public string Language { get; set; }

    /// <summary>
    /// Gets or sets the kind of track to look for in media files: <c>dub</c> for an audio
    /// track, <c>sub</c> for a subtitle track.
    /// </summary>
    public string TrackKind { get; set; }

    /// <summary>
    /// Gets or sets the source's confidence tier, where empty means the source default.
    /// </summary>
    public string ConfidenceTier { get; set; }

    /// <summary>
    /// Gets or sets the tag currently applied to the library, recorded so that changing
    /// the tracked language can remove the tag it left behind.
    /// </summary>
    public string? AppliedTagName { get; set; }

    /// <summary>
    /// Gets or sets the source, tier and language the cached dataset belongs to, so a
    /// change to any of them re-downloads rather than reusing the wrong file.
    /// </summary>
    public string? DatasetKey { get; set; }

    /// <summary>
    /// Gets or sets the ETag of the last downloaded dub dataset.
    /// </summary>
    public string DataETag { get; set; }

    /// <summary>
    /// Gets or sets the ETag of the last downloaded AniList mapping.
    /// </summary>
    public string MappingETag { get; set; }

    /// <summary>
    /// Gets or sets the time the data last changed.
    /// </summary>
    public DateTime? LastUpdatedUtc { get; set; }
}
