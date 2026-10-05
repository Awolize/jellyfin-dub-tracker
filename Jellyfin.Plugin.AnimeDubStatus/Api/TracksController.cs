using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.AnimeDubStatus.Configuration;
using Jellyfin.Plugin.AnimeDubStatus.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AnimeDubStatus.Api;

/// <summary>
/// Reports which episodes of a series carry the tracked language's track.
/// </summary>
[ApiController]
[Route("AnimeDubStatus")]
[Authorize]
public class TracksController : ControllerBase
{
    private readonly EpisodeTrackIndex _tracks;

    /// <summary>
    /// Initializes a new instance of the <see cref="TracksController"/> class.
    /// </summary>
    /// <param name="tracks">Instance of the <see cref="EpisodeTrackIndex"/> class.</param>
    public TracksController(EpisodeTrackIndex tracks)
    {
        _tracks = tracks;
    }

    /// <summary>
    /// Gets the tracked-track coverage of a series.
    /// </summary>
    /// <param name="seriesId">The series identifier.</param>
    /// <returns>The aggregate and the per-episode states.</returns>
    [HttpGet("tracks/{seriesId:guid}")]
    public ActionResult GetCoverage(Guid seriesId)
    {
        var configuration = TrackSettings.Current;
        var coverage = _tracks.GetCoverage(seriesId);

        return Ok(new
        {
            seriesId = seriesId.ToString("N", CultureInfo.InvariantCulture),
            language = TrackSettings.GetLanguage(configuration).Code,
            kind = TrackSettings.IsSubtitle(configuration) ? TrackSettings.Sub : TrackSettings.Dub,
            released = coverage.Released,
            present = coverage.Present,
            percent = coverage.Percent,
            episodes = coverage.Episodes.Select(episode => new
            {
                id = episode.Id.ToString("N", CultureInfo.InvariantCulture),
                hasTrack = episode.HasTrack,
                unaired = episode.IsUnaired,
                language = episode.Language
            })
        });
    }
}
