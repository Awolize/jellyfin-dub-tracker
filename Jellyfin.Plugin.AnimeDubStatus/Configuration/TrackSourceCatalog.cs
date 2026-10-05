using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AnimeDubStatus.Configuration;

/// <summary>
/// The data sources the plugin can read from.
/// </summary>
/// <remarks>
/// Only one bulk source ships today. The catalogue exists so that adding another is a
/// change here plus a settings entry, rather than a change to the tagging pipeline.
/// </remarks>
public static class TrackSourceCatalog
{
    /// <summary>
    /// The identifier of the MyDubList source, which is the default.
    /// </summary>
    public const string MyDubList = "mydublist";

    private static readonly TrackSource[] Sources =
    [
        new TrackSource(
            Id: MyDubList,
            DisplayName: "MyDubList",
            Attribution: "Dub data (c) MyDubList - https://mydublist.com - CC BY 4.0",
            ConfidenceTiers: ["very-high", "high", "normal", "low"],
            DefaultConfidenceTier: "normal",
            DubDataUrlFormat: "https://raw.githubusercontent.com/Joelis57/MyDubList/main/dubs/confidence/{1}/dubbed_{0}.json",
            MappingUrl: "https://raw.githubusercontent.com/Joelis57/MyDubList/main/dubs/mappings/mappings_anilist.jsonl",
            CacheFileFormat: "dubbed_{0}.json")
    ];

    /// <summary>
    /// Gets every configured source, in display order.
    /// </summary>
    public static IReadOnlyList<TrackSource> All => Sources;

    /// <summary>
    /// Gets whether an identifier names a configured source.
    /// </summary>
    /// <param name="id">The candidate identifier.</param>
    /// <returns>True when the source is known.</returns>
    public static bool IsKnown(string? id) =>
        id is not null
        && Sources.Any(source => string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a source identifier, falling back to the default.
    /// </summary>
    /// <param name="id">The configured identifier, which may be unknown.</param>
    /// <returns>The matching source, or MyDubList.</returns>
    public static TrackSource Get(string? id) =>
        Sources.FirstOrDefault(source =>
            string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Sources[0];
}
