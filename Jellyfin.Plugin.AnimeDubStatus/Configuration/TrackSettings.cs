namespace Jellyfin.Plugin.AnimeDubStatus.Configuration;

/// <summary>
/// Derives the active tag, badge label and data locations from configuration.
/// </summary>
/// <remarks>
/// The tag and the badge label follow the tracked language, so switching language
/// renames them everywhere at once.
/// </remarks>
public static class TrackSettings
{
    /// <summary>
    /// Used before the plugin instance exists, so callers never have to null-check.
    /// Treat it as read-only.
    /// </summary>
    private static readonly PluginConfiguration Fallback = new();

    /// <summary>
    /// Gets the live plugin configuration, or defaults before the plugin is constructed.
    /// </summary>
    public static PluginConfiguration Current => Plugin.Instance?.Configuration ?? Fallback;

    /// <summary>
    /// Resolves the configured source.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The matching source, or the default.</returns>
    public static TrackSource GetSource(PluginConfiguration configuration) =>
        TrackSourceCatalog.Get(configuration.Source);

    /// <summary>
    /// Resolves the configured language.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The matching language, or the default.</returns>
    public static TrackLanguage GetLanguage(PluginConfiguration configuration) =>
        TrackLanguageCatalog.Get(configuration.Language);

    /// <summary>
    /// Resolves the configured confidence tier.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The matching tier, or the source's default.</returns>
    public static string GetConfidenceTier(PluginConfiguration configuration) =>
        GetSource(configuration).GetConfidenceTier(configuration.ConfidenceTier);

    /// <summary>
    /// Gets the tag applied to library items, such as <c>German Dub Available</c>.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The tag name.</returns>
    public static string GetTagName(PluginConfiguration configuration) =>
        string.Concat(GetLanguage(configuration).DisplayName, " Dub Available");

    /// <summary>
    /// Gets the label drawn on a card, such as <c>DE DUB</c>.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The badge label.</returns>
    public static string GetBadgeLabel(PluginConfiguration configuration) =>
        string.Concat(GetLanguage(configuration).BadgeLabel, " DUB");

    /// <summary>
    /// Gets the source, tier and language combination the cached data belongs to.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>A stable key that changes when the dataset changes.</returns>
    public static string GetDatasetKey(PluginConfiguration configuration) =>
        string.Join(
            '|',
            GetSource(configuration).Id,
            GetConfidenceTier(configuration),
            GetLanguage(configuration).Code);

    /// <summary>
    /// Gets the location of the dataset for the configured language and tier.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The absolute location of the dataset.</returns>
    public static string GetDubDataLocation(PluginConfiguration configuration) =>
        GetSource(configuration).GetDubDataLocation(
            GetLanguage(configuration).Code,
            GetConfidenceTier(configuration));

    /// <summary>
    /// Gets the cache file name for the configured language.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The file name, without a directory.</returns>
    public static string GetCacheFileName(PluginConfiguration configuration) =>
        GetSource(configuration).GetCacheFileName(GetLanguage(configuration).Code);

    /// <summary>
    /// Gets the location of the AniList to MyAnimeList mapping.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The absolute location of the mapping.</returns>
    public static string GetMappingLocation(PluginConfiguration configuration) =>
        GetSource(configuration).MappingUrl;

    /// <summary>
    /// Gets the credit the configured source's licence requires.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The attribution text.</returns>
    public static string GetAttribution(PluginConfiguration configuration) =>
        GetSource(configuration).Attribution;
}
