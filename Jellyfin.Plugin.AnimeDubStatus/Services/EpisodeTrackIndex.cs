using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AnimeDubStatus.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// Reports which episodes of a series carry the tracked language's audio or subtitle track.
/// </summary>
/// <remarks>
/// This answers a different question from the tag: the tag records that a dub exists,
/// while this records whether the library actually has it, per episode, taken from the
/// media streams Jellyfin already probed.
/// </remarks>
public sealed class EpisodeTrackIndex
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly IReadOnlyDictionary<Guid, int> EmptyCoverage = new Dictionary<Guid, int>();

    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<EpisodeTrackIndex> _logger;
    private readonly ConcurrentDictionary<Guid, CachedCoverage> _cache = new();
    private volatile IReadOnlyDictionary<Guid, int> _libraryCoverage = new Dictionary<Guid, int>();
    private volatile IReadOnlyCollection<Guid> _libraryMissing = [];
    private volatile bool _hasMeasured;
    private string? _measuredKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeTrackIndex"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{EpisodeTrackIndex}"/> interface.</param>
    public EpisodeTrackIndex(
        ILibraryManager libraryManager,
        ILocalizationManager localization,
        ILogger<EpisodeTrackIndex> logger)
    {
        _libraryManager = libraryManager;
        _localization = localization;
        _logger = logger;
    }

    /// <summary>
    /// Gets the coverage measured for the language and track kind in force, containing
    /// only the series where the library holds some of the track. A series that is absent
    /// holds none of it, and a measurement made for a different selection reads as empty
    /// rather than as the wrong answer.
    /// </summary>
    public IReadOnlyDictionary<Guid, int> LibraryCoverage =>
        IsCurrentMeasurement ? _libraryCoverage : EmptyCoverage;

    /// <summary>
    /// Gets a value indicating whether a coverage pass has completed for the selection in
    /// force, so a client can tell "none of it" apart from "not looked yet".
    /// </summary>
    public bool HasMeasured => _hasMeasured && IsCurrentMeasurement;

    /// <summary>
    /// Gets the episodes missing the track, across the series the library partly holds.
    /// </summary>
    /// <remarks>
    /// Sent with the library-wide payload so that episode cards are markable on pages
    /// that are not a detail page, such as the home screen's Next Up row. Series the
    /// library holds none of are left out, since their cards already say so and marking
    /// every episode of them would be noise.
    /// </remarks>
    public IReadOnlyCollection<Guid> MissingEpisodes =>
        IsCurrentMeasurement ? _libraryMissing : [];

    /// <summary>
    /// Gets a value indicating whether the last measurement belongs to the language and
    /// track kind in force.
    /// </summary>
    private bool IsCurrentMeasurement =>
        string.Equals(_measuredKey, CurrentKey, StringComparison.Ordinal);

    /// <summary>
    /// Gets the language and track kind a measurement would cover.
    /// </summary>
    private static string CurrentKey
    {
        get
        {
            var configuration = TrackSettings.Current;
            return string.Concat(
                TrackSettings.GetLanguage(configuration).Code,
                "|",
                TrackSettings.GetStreamType(configuration));
        }
    }

    /// <summary>
    /// Gets the coverage of a series for the configured language and track kind.
    /// </summary>
    /// <param name="seriesId">The series identifier.</param>
    /// <returns>The coverage, or an empty result when the series has no episodes.</returns>
    public EpisodeCoverage GetCoverage(Guid seriesId)
    {
        var configuration = TrackSettings.Current;
        var language = TrackSettings.GetLanguage(configuration);
        var streamType = TrackSettings.GetStreamType(configuration);
        var key = string.Concat(
            seriesId.ToString("N", CultureInfo.InvariantCulture),
            "|",
            language.Code,
            "|",
            streamType);

        if (_cache.TryGetValue(seriesId, out var cached)
            && string.Equals(cached.Key, key, StringComparison.Ordinal)
            && DateTime.UtcNow - cached.BuiltUtc <= CacheLifetime)
        {
            return cached.Coverage;
        }

        var coverage = Build(seriesId, language, streamType);
        _cache[seriesId] = new CachedCoverage(key, DateTime.UtcNow, coverage);
        return coverage;
    }

    /// <summary>
    /// Builds the JSON the web client reads for one series, with an entity tag.
    /// </summary>
    /// <param name="seriesId">The series identifier.</param>
    /// <returns>The payload and an entity tag for it.</returns>
    public (string Payload, string ETag) GetSnapshot(Guid seriesId)
    {
        var configuration = TrackSettings.Current;
        var coverage = GetCoverage(seriesId);

        var payload = JsonSerializer.Serialize(new
        {
            seriesId = seriesId.ToString("N", CultureInfo.InvariantCulture),
            language = TrackSettings.GetLanguage(configuration).Code,
            kind = TrackSettings.IsSubtitle(configuration) ? TrackSettings.Sub : TrackSettings.Dub,
            labeled = TrackSettings.GetBadgeLabel(configuration),
            released = coverage.Released,
            present = coverage.Present,
            percent = coverage.Percent,
            strategy = coverage.Strategy,
            episodes = coverage.Episodes.Select(episode => new
            {
                id = episode.Id.ToString("N", CultureInfo.InvariantCulture),
                seasonId = episode.SeasonId.ToString("N", CultureInfo.InvariantCulture),
                hasTrack = episode.HasTrack,
                unaired = episode.IsUnaired,
                language = episode.Language
            })
        });

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var etag = string.Concat("\"", Convert.ToHexString(hash, 0, 8).ToLowerInvariant(), "\"");

        return (payload, etag);
    }

    /// <summary>
    /// Measures the given series and publishes the result for cheap reads.
    /// </summary>
    /// <remarks>
    /// Called from the scheduled task rather than from a request, because reading every
    /// episode's media streams is far too slow to do while answering a page load.
    /// </remarks>
    /// <param name="seriesIds">The series to measure.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public void RefreshLibrary(IReadOnlyCollection<Guid> seriesIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seriesIds);

        var measured = new Dictionary<Guid, int>(seriesIds.Count);
        var missing = new List<Guid>();

        _logger.LogInformation("Measuring coverage for {Total} series", seriesIds.Count);

        foreach (var seriesId in seriesIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // One unreadable series must not leave the whole library unmeasured, which
            // would show as every badge staying grey indefinitely.
            try
            {
                var coverage = GetCoverage(seriesId);
                if (coverage.Percent > 0)
                {
                    measured[seriesId] = coverage.Percent;

                    foreach (var episode in coverage.Episodes)
                    {
                        if (!episode.IsUnaired && !episode.HasTrack)
                        {
                            missing.Add(episode.Id);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not measure coverage for series {SeriesId}", seriesId);
            }
        }

        _libraryCoverage = measured;
        _libraryMissing = missing;
        _hasMeasured = true;
        _measuredKey = CurrentKey;

        _logger.LogInformation(
            "Measured coverage for {Total} series: {Owned} hold some of the track",
            seriesIds.Count,
            measured.Count);
    }

    /// <summary>
    /// Discards every cached coverage, after media changed or settings moved.
    /// </summary>
    public void Invalidate()
    {
        _cache.Clear();
        _libraryCoverage = new Dictionary<Guid, int>();
        _libraryMissing = [];
        _hasMeasured = false;
    }

    private EpisodeCoverage Build(Guid seriesId, TrackLanguage language, MediaStreamType streamType)
    {
        var item = _libraryManager.GetItemById(seriesId);
        var itemType = item?.GetType().Name ?? "unknown";
        var episodes = FindEpisodes(seriesId, itemType, out var strategy);

        // A playable item has no descendants to walk, so its own state belongs in the
        // answer. Without this an episode's page reports nothing about itself.
        if (item is Video video && !episodes.Any(candidate => candidate.Id == video.Id))
        {
            episodes = [video, .. episodes];
            strategy = string.Concat(itemType, "/self");
        }

        var cultures = _localization.GetCultures().ToList();
        var states = new List<EpisodeTrack>(episodes.Count);

        foreach (var episode in episodes)
        {
            var (hasTrack, streamLanguage) = Inspect(episode, language, streamType, cultures);
            states.Add(new EpisodeTrack(episode.Id, episode.ParentId, hasTrack, episode.IsUnaired, streamLanguage));
        }

        var result = TrackCoverage.Compute(states.Select(state => new EpisodeTrackFact(state.HasTrack, state.IsUnaired)));

        _logger.LogInformation(
            "Episode coverage for {ItemType} {SeriesId} ({Strategy}) in {Language} ({Kind}): {Present}/{Released} ({Percent}%)",
            itemType,
            seriesId,
            strategy,
            language.DisplayName,
            streamType,
            result.Present,
            result.Released,
            result.Percent);

        return new EpisodeCoverage(result.Released, result.Present, result.Percent, states, strategy);
    }

    /// <summary>
    /// Finds the episodes belonging to an item.
    /// </summary>
    /// <remarks>
    /// Filtering by ancestor did not return episodes in practice, so this walks the item
    /// tree by parent instead: episodes below a season, then episodes parented straight to
    /// the item, which covers libraries with and without season folders.
    /// </remarks>
    /// <param name="id">The series, season or folder identifier.</param>
    /// <param name="itemType">The resolved item type, for the strategy label.</param>
    /// <param name="strategy">How the episodes were found.</param>
    /// <returns>The episodes, possibly empty.</returns>
    private IReadOnlyList<BaseItem> FindEpisodes(Guid id, string itemType, out string strategy)
    {
        var byAncestor = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            AncestorIds = [id]
        });

        if (byAncestor.Count > 0)
        {
            strategy = string.Concat(itemType, "/ancestors");
            return byAncestor;
        }

        var seasons = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Season],
            ParentId = id
        });

        if (seasons.Count > 0)
        {
            var fromSeasons = new List<BaseItem>();
            foreach (var season in seasons)
            {
                fromSeasons.AddRange(_libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    ParentId = season.Id
                }));
            }

            if (fromSeasons.Count > 0)
            {
                strategy = string.Concat(itemType, "/seasons");
                return fromSeasons;
            }
        }

        var flat = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            ParentId = id
        });

        strategy = string.Concat(itemType, flat.Count > 0 ? "/flat" : "/none");
        return flat;
    }

    private (bool HasTrack, string? Language) Inspect(
        BaseItem episode,
        TrackLanguage language,
        MediaStreamType streamType,
        IReadOnlyList<CultureDto> cultures)
    {
        var sources = episode.GetMediaSources(enablePathSubstitution: false);
        string? closest = null;

        foreach (var source in sources)
        {
            if (source.MediaStreams is null)
            {
                continue;
            }

            foreach (var stream in source.MediaStreams)
            {
                if (stream.Type != streamType || string.IsNullOrWhiteSpace(stream.Language))
                {
                    continue;
                }

                closest ??= stream.Language;

                if (MatchesLanguage(stream.Language, language, cultures))
                {
                    return (true, stream.Language);
                }
            }
        }

        return (false, closest);
    }

    /// <summary>
    /// Matches a stream's language against a tracked language, tolerating the different
    /// codes a container can carry: <c>de</c>, <c>ger</c>, <c>deu</c> and region forms.
    /// </summary>
    /// <param name="streamLanguage">The language tag from the media stream.</param>
    /// <param name="language">The tracked language.</param>
    /// <param name="cultures">Jellyfin's culture table.</param>
    /// <returns>True when the stream is in the tracked language.</returns>
    private static bool MatchesLanguage(
        string streamLanguage,
        TrackLanguage language,
        IReadOnlyList<CultureDto> cultures)
    {
        var value = streamLanguage.Trim();

        // Strip any region suffix, for example pt-BR.
        var separator = value.IndexOf('-', StringComparison.Ordinal);
        if (separator > 0)
        {
            value = value[..separator];
        }

        var isoCode = language.BadgeLabel.ToLowerInvariant();

        foreach (var culture in cultures)
        {
            var isOurCulture =
                string.Equals(culture.TwoLetterISOLanguageName, isoCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(culture.ThreeLetterISOLanguageName, isoCode, StringComparison.OrdinalIgnoreCase)
                || culture.ThreeLetterISOLanguageNames.Contains(isoCode, StringComparer.OrdinalIgnoreCase);

            if (!isOurCulture)
            {
                continue;
            }

            if (string.Equals(culture.TwoLetterISOLanguageName, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(culture.ThreeLetterISOLanguageName, value, StringComparison.OrdinalIgnoreCase)
                || culture.ThreeLetterISOLanguageNames.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Fall back to a direct comparison, so an incomplete culture table cannot hide a match.
        return string.Equals(isoCode, value, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CachedCoverage(string Key, DateTime BuiltUtc, EpisodeCoverage Coverage);
}
