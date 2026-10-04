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
    }

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
