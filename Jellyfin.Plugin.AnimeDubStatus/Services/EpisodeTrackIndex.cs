using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<EpisodeTrackIndex> _logger;
    private readonly ConcurrentDictionary<Guid, CachedCoverage> _cache = new();

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
    /// Discards every cached coverage, after media changed or settings moved.
    /// </summary>
    public void Invalidate()
    {
        _cache.Clear();
    }

    private EpisodeCoverage Build(Guid seriesId, TrackLanguage language, MediaStreamType streamType)
    {
        var item = _libraryManager.GetItemById(seriesId);
        var itemType = item?.GetType().Name ?? "unknown";
        var episodes = FindEpisodes(seriesId, itemType, out var strategy);

        var cultures = _localization.GetCultures().ToList();
        var states = new List<EpisodeTrack>(episodes.Count);

        foreach (var episode in episodes)
        {
            var (hasTrack, streamLanguage) = Inspect(episode, language, streamType, cultures);
            states.Add(new EpisodeTrack(episode.Id, hasTrack, episode.IsUnaired, streamLanguage));
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
