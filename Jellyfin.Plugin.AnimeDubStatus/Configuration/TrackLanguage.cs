namespace Jellyfin.Plugin.AnimeDubStatus.Configuration;

/// <summary>
/// A language whose dubs can be tracked.
/// </summary>
/// <param name="Code">The data source's language code, such as <c>german</c>.</param>
/// <param name="DisplayName">The English name, used in the tag, such as <c>German</c>.</param>
/// <param name="BadgeLabel">The short label drawn on cards, such as <c>DE</c>.</param>
public sealed record TrackLanguage(string Code, string DisplayName, string BadgeLabel);
