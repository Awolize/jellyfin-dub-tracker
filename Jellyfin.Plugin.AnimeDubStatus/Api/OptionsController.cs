using System.Linq;
using Jellyfin.Plugin.AnimeDubStatus.Configuration;
using Jellyfin.Plugin.AnimeDubStatus.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AnimeDubStatus.Api;

/// <summary>
/// Serves the available sources, languages and tiers so the settings page cannot drift
/// from what the plugin actually supports.
/// </summary>
[ApiController]
[Route("AnimeDubStatus")]
[Authorize]
public class OptionsController : ControllerBase
{
    private readonly DubDataService _dubData;
    private readonly DubStatusIndex _statusIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptionsController"/> class.
    /// </summary>
    /// <param name="dubData">Instance of the <see cref="DubDataService"/> class.</param>
    /// <param name="statusIndex">Instance of the <see cref="DubStatusIndex"/> class.</param>
    public OptionsController(DubDataService dubData, DubStatusIndex statusIndex)
    {
        _dubData = dubData;
        _statusIndex = statusIndex;
    }

    /// <summary>
    /// Gets the settings choices and the current state.
    /// </summary>
    /// <returns>The catalogue and the active selection.</returns>
    [HttpGet("options")]
    public ActionResult GetOptions()
    {
        var configuration = TrackSettings.Current;

        return Ok(new
        {
            sources = TrackSourceCatalog.All.Select(source => new
            {
                id = source.Id,
                name = source.DisplayName,
                attribution = source.Attribution,
                tiers = source.ConfidenceTiers,
                defaultTier = source.DefaultConfidenceTier
            }),
            languages = TrackLanguageCatalog.All.Select(language => new
            {
                code = language.Code,
                name = language.DisplayName,
                label = language.BadgeLabel
            }),
            current = new
            {
                source = TrackSettings.GetSource(configuration).Id,
                language = TrackSettings.GetLanguage(configuration).Code,
                confidenceTier = TrackSettings.GetConfidenceTier(configuration),
                tagName = TrackSettings.GetTagName(configuration),
                badgeLabel = TrackSettings.GetBadgeLabel(configuration),
                attribution = TrackSettings.GetAttribution(configuration),
                appliedTagName = configuration.AppliedTagName,
                lastUpdatedUtc = configuration.LastUpdatedUtc,
                dubbedTitleCount = _dubData.DubbedTitleCount,

                // Separates "the dataset has this language" from "this library has
                // matches", which is otherwise invisible when a language yields no badges.
                taggedSeriesCount = _statusIndex.GetSnapshot().Count
            }
        });
    }
}
