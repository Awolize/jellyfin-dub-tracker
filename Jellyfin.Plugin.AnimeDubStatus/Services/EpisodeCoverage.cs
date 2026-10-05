using System.Collections.Generic;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// A series' coverage of the tracked track, with the per-episode detail behind it.
/// </summary>
/// <param name="Released">How many episodes count, that is, have aired.</param>
/// <param name="Present">How many of those carry the track.</param>
/// <param name="Percent">Present as a percentage of released.</param>
/// <param name="Episodes">The per-episode states.</param>
public sealed record EpisodeCoverage(
    int Released,
    int Present,
    int Percent,
    IReadOnlyList<EpisodeTrack> Episodes);
