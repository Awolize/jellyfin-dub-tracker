using System;
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
    private readonly ILogger<DubStatusIndex> _logger;
    private readonly object _sync = new();

    private string _payload = "{\"label\":\"\",\"ids\":[]}";
    private string _etag = "\"0\"";
    private DateTime _builtUtc = DateTime.MinValue;
    private string? _snapshotKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="DubStatusIndex"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{DubStatusIndex}"/> interface.</param>
    public DubStatusIndex(ILibraryManager libraryManager, ILogger<DubStatusIndex> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
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
    /// Gets the current snapshot of dubbed series identifiers and the badge label.
    /// </summary>
    /// <returns>The JSON payload and an entity tag for it.</returns>
    public (string Payload, string ETag) GetSnapshot()
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

            return (_payload, _etag);
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

        var payload = string.Concat(
            "{\"label\":",
            JsonSerializer.Serialize(label),
            ",\"ids\":",
            JsonSerializer.Serialize(ids),
            "}");

        _payload = payload;
        _etag = ComputeETag(Encoding.UTF8.GetBytes(payload));
        _builtUtc = DateTime.UtcNow;
        _snapshotKey = key;

        _logger.LogInformation(
            "Dub status index rebuilt for {Tag}: {Count} series",
            tagName,
            ids.Length);
    }
}
