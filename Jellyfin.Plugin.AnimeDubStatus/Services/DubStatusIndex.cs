using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// Caches the library series that currently carry the English dub tag.
/// </summary>
/// <remarks>
/// Dub status is a library-wide fact rather than a per-user one, so the web client
/// can be handed one shared list instead of querying the Jellyfin API itself.
/// </remarks>
public sealed class DubStatusIndex
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly string[] TagFilter = [DubTagger.TagName];

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DubStatusIndex> _logger;
    private readonly object _sync = new();

    private string _payload = "[]";
    private string _etag = "\"0\"";
    private DateTime _builtUtc = DateTime.MinValue;

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
        }
    }

    /// <summary>
    /// Gets the current snapshot of dubbed series identifiers.
    /// </summary>
    /// <returns>The JSON payload and an entity tag for it.</returns>
    public (string Payload, string ETag) GetSnapshot()
    {
        lock (_sync)
        {
            if (_builtUtc == DateTime.MinValue || DateTime.UtcNow - _builtUtc > CacheLifetime)
            {
                Rebuild();
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

    private void Rebuild()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true,
            Tags = TagFilter
        });

        // Dashless lowercase so the client can compare directly against a card's data-id.
        var ids = items
            .Select(item => item.Id.ToString("N", CultureInfo.InvariantCulture))
            .ToArray();

        var payload = JsonSerializer.Serialize(ids);
        var bytes = Encoding.UTF8.GetBytes(payload);

        _payload = payload;
        _etag = ComputeETag(bytes);
        _builtUtc = DateTime.UtcNow;

        _logger.LogInformation(
            "Dub status index rebuilt: {Count} tagged series",
            ids.Length);
    }
}
