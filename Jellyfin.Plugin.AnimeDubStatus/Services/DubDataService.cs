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
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// Downloads, caches and queries the MyDubList English dub dataset and AniList mapping.
/// Dub data © MyDubList - https://mydublist.com - (CC BY 4.0).
/// </summary>
public sealed class DubDataService : IDisposable
{
    private const string DubUrl = "https://raw.githubusercontent.com/Joelis57/MyDubList/main/dubs/confidence/normal/dubbed_english.json";
    private const string MappingUrl = "https://raw.githubusercontent.com/Joelis57/MyDubList/main/dubs/mappings/mappings_anilist.jsonl";
    private const int MinimumDubEntries = 1000;
    private const int MinimumMappingEntries = 5000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DubDataService> _logger;
    private readonly string _dubCachePath;
    private readonly string _mappingCachePath;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private volatile FrozenSet<int> _englishDubs = FrozenSet<int>.Empty;
    private volatile FrozenDictionary<int, int> _anilistToMal = FrozenDictionary<int, int>.Empty;

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

        var directory = Path.Combine(applicationPaths.DataPath, "AnimeDubStatus");
        Directory.CreateDirectory(directory);
        _dubCachePath = Path.Combine(directory, "dubbed_english.json");
        _mappingCachePath = Path.Combine(directory, "mappings_anilist.jsonl");

        LoadFromDisk();
    }

    /// <summary>
    /// Gets the number of English dubbed titles currently loaded.
    /// </summary>
    public int EnglishDubCount => _englishDubs.Count;

    /// <summary>
    /// Checks whether a MyAnimeList ID has an English dub.
    /// </summary>
    /// <param name="malId">The MyAnimeList ID.</param>
    /// <returns>True if an English dub is listed.</returns>
    public bool IsEnglishDubbed(int malId) => _englishDubs.Contains(malId);

    /// <summary>
    /// Converts an AniList ID to a MyAnimeList ID.
    /// </summary>
    /// <param name="anilistId">The AniList ID.</param>
    /// <param name="malId">The MyAnimeList ID, if known.</param>
    /// <returns>True if a mapping exists.</returns>
    public bool TryGetMalId(int anilistId, out int malId) => _anilistToMal.TryGetValue(anilistId, out malId);

    /// <summary>
    /// Downloads the dataset and mapping if they changed since the last check.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if anything new was downloaded.</returns>
    public async Task<bool> UpdateAsync(CancellationToken cancellationToken)
    {
        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance;
            var dubsChanged = await UpdateDubsAsync(plugin, cancellationToken).ConfigureAwait(false);
            var mappingChanged = await UpdateMappingAsync(plugin, cancellationToken).ConfigureAwait(false);

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

    private static FrozenSet<int> ParseDubs(byte[] json)
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

        return ids.ToFrozenSet();
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

    private async Task<bool> UpdateDubsAsync(Plugin? plugin, CancellationToken cancellationToken)
    {
        var fetched = await FetchAsync(DubUrl, plugin?.Configuration.DataETag, _dubCachePath, cancellationToken).ConfigureAwait(false);
        if (fetched is null)
        {
            _logger.LogInformation("English dub data is already up to date");
            return false;
        }

        var (bytes, etag) = fetched.Value;
        var parsed = ParseDubs(bytes);
        if (parsed.Count < MinimumDubEntries)
        {
            throw new InvalidDataException($"Dub data has only {parsed.Count} entries, refusing to use it");
        }

        await WriteCacheAsync(_dubCachePath, bytes, cancellationToken).ConfigureAwait(false);
        _englishDubs = parsed;
        if (plugin is not null)
        {
            plugin.Configuration.DataETag = etag;
        }

        _logger.LogInformation("Loaded {Count} English dubbed titles", parsed.Count);
        return true;
    }

    private async Task<bool> UpdateMappingAsync(Plugin? plugin, CancellationToken cancellationToken)
    {
        var fetched = await FetchAsync(MappingUrl, plugin?.Configuration.MappingETag, _mappingCachePath, cancellationToken).ConfigureAwait(false);
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
        string url,
        string? knownETag,
        string cachePath,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
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

    private void LoadFromDisk()
    {
        try
        {
            if (File.Exists(_dubCachePath))
            {
                _englishDubs = ParseDubs(File.ReadAllBytes(_dubCachePath));
                _logger.LogInformation("Loaded {Count} English dubbed titles from cache", _englishDubs.Count);
            }

            if (File.Exists(_mappingCachePath))
            {
                _anilistToMal = ParseMapping(File.ReadAllBytes(_mappingCachePath));
                _logger.LogInformation("Loaded {Count} AniList mappings from cache", _anilistToMal.Count);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Could not read cached data, it will be re-downloaded");
        }
    }
}
