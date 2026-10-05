using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeDubStatus.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// Downloads, caches and queries the configured source's dub dataset and the AniList mapping.
/// Dub data (c) MyDubList - https://mydublist.com - (CC BY 4.0).
/// </summary>
public sealed class DubDataService : IDisposable
{
    /// <summary>
    /// The mapping is a large, language-independent file, so a tiny one means a bad response.
    /// </summary>
    private const int MinimumMappingEntries = 500;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DubDataService> _logger;
    private readonly string _directory;
    private readonly string _mappingCachePath;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private volatile FrozenSet<int> _dubbedTitles = FrozenSet<int>.Empty;
    private volatile FrozenDictionary<int, int> _anilistToMal = FrozenDictionary<int, int>.Empty;
    private string? _loadedDatasetKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="DubDataService"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{DubDataService}"/> interface.</param>
    public DubDataService(
        IApplicationPaths applicationPaths,
        IHttpClientFactory httpClientFactory,
        ILogger<DubDataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _directory = Path.Combine(applicationPaths.DataPath, "AnimeDubStatus");
        Directory.CreateDirectory(_directory);
        _mappingCachePath = Path.Combine(_directory, "mappings_anilist.jsonl");
    }

    /// <summary>
    /// Gets the number of titles with a dub in the configured language.
    /// </summary>
    public int DubbedTitleCount
    {
        get
        {
            EnsureLoaded();
            return _dubbedTitles.Count;
        }
    }

    /// <summary>
    /// Checks whether a MyAnimeList ID has a dub in the configured language.
    /// </summary>
    /// <param name="malId">The MyAnimeList ID.</param>
    /// <returns>True if a dub is listed.</returns>
    public bool IsDubbed(int malId)
    {
        EnsureLoaded();
        return _dubbedTitles.Contains(malId);
    }

    /// <summary>
    /// Converts an AniList ID to a MyAnimeList ID.
    /// </summary>
    /// <param name="anilistId">The AniList ID.</param>
    /// <param name="malId">The MyAnimeList ID, if known.</param>
    /// <returns>True if a mapping exists.</returns>
    public bool TryGetMalId(int anilistId, out int malId)
    {
        EnsureLoaded();
        return _anilistToMal.TryGetValue(anilistId, out malId);
    }

    /// <summary>
    /// Downloads the dataset and mapping if they changed, and reloads them when the
    /// configured source, tier or language changed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if anything new was downloaded.</returns>
    public async Task<bool> UpdateAsync(CancellationToken cancellationToken)
    {
        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance;
            var configuration = TrackSettings.Current;
            var datasetKey = TrackSettings.GetDatasetKey(configuration);

            // Switching source, tier or language invalidates the cached dataset and the
            // ETag that belongs to it, so the new file is fetched rather than assumed.
            if (!string.Equals(configuration.DatasetKey, datasetKey, StringComparison.Ordinal))
            {
                _logger.LogInformation("Tracked dataset changed to {DatasetKey}", datasetKey);
                configuration.DatasetKey = datasetKey;
                configuration.DataETag = string.Empty;

                // Force a reload from this language's own cache file.
                _loadedDatasetKey = null;
                plugin?.SaveConfiguration();
            }

            var dubsChanged = await UpdateDubsAsync(plugin, configuration, cancellationToken).ConfigureAwait(false);
            var mappingChanged = await UpdateMappingAsync(plugin, configuration, cancellationToken).ConfigureAwait(false);

            if ((dubsChanged || mappingChanged) && plugin is not null)
            {
                plugin.Configuration.LastUpdatedUtc = DateTime.UtcNow;
                plugin.SaveConfiguration();
            }

            return dubsChanged || mappingChanged;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _updateLock.Dispose();
    }

    private static (FrozenSet<int> Ids, int PartialCount) ParseDubbed(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("dubbed", out var dubbed)
            || dubbed.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Dub data is missing the 'dubbed' array");
        }

        var ids = new HashSet<int>();
        foreach (var element in dubbed.EnumerateArray())
        {
            if (element.TryGetInt32(out var id))
            {
                ids.Add(id);
            }
        }

        var partial = document.RootElement.TryGetProperty("partial", out var partialElement)
            && partialElement.ValueKind == JsonValueKind.Array
                ? partialElement.GetArrayLength()
                : 0;

        return (ids.ToFrozenSet(), partial);
    }

    private static FrozenDictionary<int, int> ParseMapping(byte[] jsonLines)
    {
        var map = new Dictionary<int, int>();
        var text = Encoding.UTF8.GetString(jsonLines);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("mal_id", out var mal) && mal.TryGetInt32(out var malId)
                && root.TryGetProperty("anilist_id", out var anilist) && anilist.TryGetInt32(out var anilistId))
            {
                map[anilistId] = malId;
            }
        }

        return map.ToFrozenDictionary();
    }

    private static async Task WriteCacheAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        // Write to a temp file and swap, so a bad download never replaces a working cache.
        var tempPath = path + ".tmp";
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    private async Task<bool> UpdateDubsAsync(
        Plugin? plugin,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var language = TrackSettings.GetLanguage(configuration).DisplayName;
        var location = TrackSettings.GetDubDataLocation(configuration);
        var cachePath = Path.Combine(_directory, TrackSettings.GetCacheFileName(configuration));

        var fetched = await FetchAsync(location, configuration.DataETag, cachePath, cancellationToken).ConfigureAwait(false);
        if (fetched is null)
        {
            _logger.LogInformation("{Language} dub data is already up to date", language);
            return false;
        }

        var (bytes, etag) = fetched.Value;

        // Parse before caching. A truncated or error response fails here, while a
        // validly empty dataset is accepted: several languages MyDubList publishes have
        // no titles yet, and treating those as corrupt would break those languages.
        var (ids, partialCount) = ParseDubbed(bytes);

        await WriteCacheAsync(cachePath, bytes, cancellationToken).ConfigureAwait(false);
        _dubbedTitles = ids;
        _loadedDatasetKey = TrackSettings.GetDatasetKey(configuration);
        if (plugin is not null)
        {
            plugin.Configuration.DataETag = etag;
        }

        _logger.LogInformation("Loaded {Count} {Language} dubbed titles", ids.Count, language);

        if (partialCount > 0)
        {
            _logger.LogInformation(
                "The {Language} dataset also lists {Count} partially dubbed titles, which this plugin does not use yet",
                language,
                partialCount);
        }

        return true;
    }

    private async Task<bool> UpdateMappingAsync(
        Plugin? plugin,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var fetched = await FetchAsync(
            TrackSettings.GetMappingLocation(configuration),
            configuration.MappingETag,
            _mappingCachePath,
            cancellationToken).ConfigureAwait(false);

        if (fetched is null)
        {
            _logger.LogInformation("AniList mapping is already up to date");
            return false;
        }

        var (bytes, etag) = fetched.Value;
        var parsed = ParseMapping(bytes);
        if (parsed.Count < MinimumMappingEntries)
        {
            throw new InvalidDataException($"Mapping has only {parsed.Count} entries, refusing to use it");
        }

        await WriteCacheAsync(_mappingCachePath, bytes, cancellationToken).ConfigureAwait(false);
        _anilistToMal = parsed;
        if (plugin is not null)
        {
            plugin.Configuration.MappingETag = etag;
        }

        _logger.LogInformation("Loaded {Count} AniList to MAL mappings", parsed.Count);
        return true;
    }

    private async Task<(byte[] Bytes, string ETag)?> FetchAsync(
        string location,
        string? knownETag,
        string cachePath,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, location);
        if (!string.IsNullOrEmpty(knownETag) && File.Exists(cachePath))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", knownETag);
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return (bytes, response.Headers.ETag?.Tag ?? string.Empty);
    }

    /// <summary>
    /// Loads the cached dataset for the configured selection, once per selection.
    /// </summary>
    private void EnsureLoaded()
    {
        var configuration = TrackSettings.Current;
        var datasetKey = TrackSettings.GetDatasetKey(configuration);
        if (string.Equals(_loadedDatasetKey, datasetKey, StringComparison.Ordinal))
        {
            return;
        }

        _loadedDatasetKey = datasetKey;
        LoadDubCache(TrackSettings.GetCacheFileName(configuration));
        LoadMappingCache();
    }

    private void LoadDubCache(string cacheFileName)
    {
        var path = Path.Combine(_directory, cacheFileName);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var (ids, _) = ParseDubbed(File.ReadAllBytes(path));
            _dubbedTitles = ids;
            _logger.LogInformation(
                "Loaded {Count} cached {Language} dubbed titles",
                ids.Count,
                TrackSettings.GetLanguage(TrackSettings.Current).DisplayName);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Could not read the cached dub data, it will be downloaded again");
        }
    }

    private void LoadMappingCache()
    {
        if (!File.Exists(_mappingCachePath))
        {
            return;
        }

        try
        {
            _anilistToMal = ParseMapping(File.ReadAllBytes(_mappingCachePath));
            _logger.LogInformation("Loaded {Count} cached AniList mappings", _anilistToMal.Count);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Could not read the cached mapping, it will be downloaded again");
        }
    }
}
