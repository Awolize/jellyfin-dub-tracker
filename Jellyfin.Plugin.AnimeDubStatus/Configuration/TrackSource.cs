using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.AnimeDubStatus.Configuration;

/// <summary>
/// A data source that publishes a bulk dataset of dubbed titles.
/// </summary>
/// <remarks>
/// This models bulk sources, which are downloaded once and indexed locally. A source
/// that has to be queried per title, such as a subtitle API, needs different treatment
/// and should not be forced into this shape.
/// </remarks>
/// <param name="Id">The stable identifier stored in configuration.</param>
/// <param name="DisplayName">The name shown in the settings page.</param>
/// <param name="Attribution">The credit the source's licence requires.</param>
/// <param name="ConfidenceTiers">The confidence tiers the source publishes.</param>
/// <param name="DefaultConfidenceTier">The tier used when none is configured.</param>
/// <param name="DubDataUrlFormat">A format string taking the language code, then the tier.</param>
/// <param name="MappingUrl">Where the AniList to MyAnimeList mapping lives.</param>
/// <param name="CacheFileFormat">A format string taking the language code.</param>
public sealed record TrackSource(
    string Id,
    string DisplayName,
    string Attribution,
    IReadOnlyList<string> ConfidenceTiers,
    string DefaultConfidenceTier,
    string DubDataUrlFormat,
    string MappingUrl,
    string CacheFileFormat)
{
    /// <summary>
    /// Builds the dataset location for a language and tier.
    /// </summary>
    /// <param name="languageCode">The source's language code.</param>
    /// <param name="tier">The confidence tier.</param>
    /// <returns>The absolute location of the dataset.</returns>
    public string GetDubDataLocation(string languageCode, string tier) =>
        string.Format(CultureInfo.InvariantCulture, DubDataUrlFormat, languageCode, tier);

    /// <summary>
    /// Builds the cache file name for a language.
    /// </summary>
    /// <param name="languageCode">The source's language code.</param>
    /// <returns>The file name, without a directory.</returns>
    public string GetCacheFileName(string languageCode) =>
        string.Format(CultureInfo.InvariantCulture, CacheFileFormat, languageCode);

    /// <summary>
    /// Resolves a configured tier to one this source publishes.
    /// </summary>
    /// <param name="tier">The configured tier, which may be empty or unknown.</param>
    /// <returns>The matching tier, or this source's default.</returns>
    public string GetConfidenceTier(string? tier) =>
        ConfidenceTiers.FirstOrDefault(candidate =>
            string.Equals(candidate, tier, StringComparison.OrdinalIgnoreCase))
        ?? DefaultConfidenceTier;
}
