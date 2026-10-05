using System;
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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// Caches the library series that carry the tracked language's dub tag.
/// </summary>
/// <remarks>
/// Dub status is a library-wide fact rather than a per-user one, so the web client
/// can be handed one shared list instead of querying the Jellyfin API itself.
/// </remarks>
public sealed class DubStatusIndex
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _libraryManager;
    private readonly EpisodeTrackIndex _tracks;
    private readonly ILogger<DubStatusIndex> _logger;
    private readonly object _sync = new();

    private string _payload = "{\"label\":\"\",\"ids\":[]}";
    private string _etag = "\"0\"";
    private DateTime _builtUtc = DateTime.MinValue;
    private string? _snapshotKey;
    private Guid[] _taggedIds = [];
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="DubStatusIndex"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="tracks">Instance of the <see cref="EpisodeTrackIndex"/> class.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{DubStatusIndex}"/> interface.</param>
    public DubStatusIndex(
        ILibraryManager libraryManager,
        EpisodeTrackIndex tracks,
        ILogger<DubStatusIndex> logger)
    {
        _libraryManager = libraryManager;
        _tracks = tracks;
        _logger = logger;
    }

    /// <summary>
    /// Gets the series currently carrying the tag, building the snapshot when needed.
    /// </summary>
    /// <returns>The tagged series identifiers.</returns>
    public IReadOnlyList<Guid> GetTaggedSeriesIds()
    {
        GetSnapshot();
        return _taggedIds;
    }

    /// <summary>
    /// Discards the cached snapshot so the next read rebuilds it.
    /// </summary>
    public void Invalidate()
    {
        lock (_sync)
        {
            _builtUtc = DateTime.MinValue;
            _snapshotKey = null;
        }
    }

    /// <summary>
    /// Gets the current snapshot: the badge label, the dubbed series identifiers, and how
    /// many the library has.
    /// </summary>
    /// <returns>The JSON payload, an entity tag, and the tagged series count.</returns>
    public (string Payload, string ETag, int Count) GetSnapshot()
    {
        var configuration = TrackSettings.Current;
        var tagName = TrackSettings.GetTagName(configuration);
        var label = TrackSettings.GetBadgeLabel(configuration);

        // The tag and the label both follow the tracked language, so a snapshot built
        // for a different selection must not be served.
        var key = string.Concat(tagName, "|", label);

        lock (_sync)
        {
            if (!string.Equals(_snapshotKey, key, StringComparison.Ordinal)
                || _builtUtc == DateTime.MinValue
                || DateTime.UtcNow - _builtUtc > CacheLifetime)
            {
                Rebuild(tagName, label, key);
            }

            return (_payload, _etag, _count);
        }
    }

    private static string ComputeETag(byte[] payload)
    {
        var hash = SHA256.HashData(payload);
        return string.Concat(
            "\"",
            Convert.ToHexString(hash, 0, 8).ToLowerInvariant(),
            "\"");
    }

    private void Rebuild(string tagName, string label, string key)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true,
            Tags = [tagName]
        });

        // Dashless lowercase so the client can compare directly against a card's data-id.
        var ids = items
            .Select(item => item.Id.ToString("N", CultureInfo.InvariantCulture))
            .ToArray();

        _taggedIds = items.Select(item => item.Id).ToArray();

        // How much of each tagged series the library actually holds. Measured during the
        // scheduled run, so this is a dictionary read rather than a per-episode scan.
        var coverage = _tracks.LibraryCoverage;
        var covered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (coverage.TryGetValue(item.Id, out var percent))
            {
                covered[item.Id.ToString("N", CultureInfo.InvariantCulture)] = percent;
            }
        }

        // Episodes missing the track within the series the library partly holds, so that
        // episode cards can be marked on pages that are not a detail page.
        var missing = _tracks.MissingEpisodes
            .Select(id => id.ToString("N", CultureInfo.InvariantCulture))
            .ToArray();

        var payload = string.Concat(
            "{\"label\":",
            JsonSerializer.Serialize(label),
            ",\"measured\":",
            _tracks.HasMeasured ? "true" : "false",
            ",\"ids\":",
            JsonSerializer.Serialize(ids),
            ",\"covered\":",
            JsonSerializer.Serialize(covered),
            ",\"missing\":",
            JsonSerializer.Serialize(missing),
            "}");

        _payload = payload;
        _etag = ComputeETag(Encoding.UTF8.GetBytes(payload));
        _builtUtc = DateTime.UtcNow;
        _snapshotKey = key;
        _count = ids.Length;

        _logger.LogInformation(
            "Dub status index rebuilt for {Tag}: {Count} series",
            tagName,
            ids.Length);
    }
}
