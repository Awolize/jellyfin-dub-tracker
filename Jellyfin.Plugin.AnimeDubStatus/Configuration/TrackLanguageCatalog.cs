using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AnimeDubStatus.Configuration;

/// <summary>
/// The languages offered by the shipped data sources.
/// </summary>
/// <remarks>
/// Display names are taken from the source datasets themselves rather than derived
/// from the codes, because they do not always match: the <c>chinese</c> dataset is
/// labelled <c>Mandarin</c> and <c>portuguese</c> is labelled <c>Portuguese (BR)</c>.
/// </remarks>
public static class TrackLanguageCatalog
{
    /// <summary>
    /// The code used when nothing else is configured.
    /// </summary>
    public const string Default = "english";

    private static readonly TrackLanguage[] Languages =
    [
        new("arabic", "Arabic", "AR"),
        new("catalan", "Catalan", "CA"),
        new("chinese", "Mandarin", "ZH"),
        new("danish", "Danish", "DA"),
        new("dutch", "Dutch", "NL"),
        new("english", "English", "EN"),
        new("finnish", "Finnish", "FI"),
        new("french", "French", "FR"),
        new("german", "German", "DE"),
        new("hebrew", "Hebrew", "HE"),
        new("hindi", "Hindi", "HI"),
        new("hungarian", "Hungarian", "HU"),
        new("indonesian", "Indonesian", "ID"),
        new("italian", "Italian", "IT"),
        new("japanese", "Japanese", "JA"),
        new("korean", "Korean", "KO"),
        new("lithuanian", "Lithuanian", "LT"),
        new("norwegian", "Norwegian", "NB"),
        new("polish", "Polish", "PL"),
        new("portuguese", "Portuguese (BR)", "PT"),
        new("russian", "Russian", "RU"),
        new("spanish", "Spanish", "ES"),
        new("swedish", "Swedish", "SV"),
        new("tagalog", "Tagalog", "TL"),
        new("thai", "Thai", "TH"),
        new("turkish", "Turkish", "TR"),
        new("vietnamese", "Vietnamese", "VI")
    ];

    /// <summary>
    /// Gets every supported language, in display order.
    /// </summary>
    public static IReadOnlyList<TrackLanguage> All => Languages;

    /// <summary>
    /// Gets whether a code names a supported language.
    /// </summary>
    /// <param name="code">The candidate code.</param>
    /// <returns>True when the code is known.</returns>
    public static bool IsKnown(string? code) =>
        code is not null
        && Languages.Any(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a language code, falling back to the default.
    /// </summary>
    /// <param name="code">The configured code, which may be unknown.</param>
    /// <returns>The matching language, or English.</returns>
    public static TrackLanguage Get(string? code) =>
        Languages.FirstOrDefault(language =>
            string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase))
        ?? Languages.First(language => language.Code == Default);
}
