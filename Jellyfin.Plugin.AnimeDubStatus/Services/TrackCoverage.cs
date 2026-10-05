using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// Works out how much of a set of episodes carries the tracked track.
/// </summary>
/// <remarks>
/// Episodes that have not aired are excluded from the denominator, so a season that is
/// still releasing does not read as half-finished when half of it simply does not exist
/// yet.
/// </remarks>
public static class TrackCoverage
{
    /// <summary>
    /// Computes the coverage of a set of episodes.
    /// </summary>
    /// <param name="episodes">The episodes to measure.</param>
    /// <returns>The aggregate, with a percentage of zero when nothing has aired.</returns>
    public static TrackCoverageResult Compute(IEnumerable<EpisodeTrackFact> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);

        var released = 0;
        var present = 0;

        foreach (var episode in episodes)
        {
            if (episode.IsUnaired)
            {
                continue;
            }

            released++;
            if (episode.HasTrack)
            {
                present++;
            }
        }

        var percent = released == 0
            ? 0
            : (int)Math.Round(100.0 * present / released, MidpointRounding.AwayFromZero);

        return new TrackCoverageResult(released, present, percent);
    }
}
