using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// Downloads, caches and queries the MyDubList English dub dataset.
/// Dub data © MyDubList - https://mydublist.com - (CC BY 4.0).
/// </summary>
public sealed class DubDataService : IDisposable
{
    private const string DataUrl = "https://raw.githubusercontent.com/Joelis57/MyDubList/main/dubs/confidence/normal/dubbed_english.json";
    private const int MinimumExpectedEntries = 1000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DubDataService> _logger;
    private readonly string _cacheFilePath;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private volatile FrozenSet<int> _englishDubs = FrozenSet<int>.Empty;

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
        _cacheFilePath = Path.Combine(directory, "dubbed_english.json");

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
    /// Downloads the dataset if it changed since the last check.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if new data was downloaded, false if already up to date.</returns>
    public async Task<bool> UpdateAsync(CancellationToken cancellationToken)
    {
        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance;

            using var request = new HttpRequestMessage(HttpMethod.Get, DataUrl);
            if (plugin is not null
                && !string.IsNullOrEmpty(plugin.Configuration.DataETag)
                && File.Exists(_cacheFilePath))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", plugin.Configuration.DataETag);
            }

            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                _logger.LogInformation("English dub data is already up to date");
                return false;
            }

            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var parsed = Parse(bytes);
            if (parsed.Count < MinimumExpectedEntries)
            {
                throw new InvalidDataException($"Dub data has only {parsed.Count} entries, refusing to use it");
            }

            // Write to a temp file and swap, so a bad download never replaces a working cache.
            var tempPath = _cacheFilePath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _cacheFilePath, overwrite: true);

            _englishDubs = parsed;

            if (plugin is not null)
            {
                plugin.Configuration.DataETag = response.Headers.ETag?.Tag ?? string.Empty;
                plugin.Configuration.LastUpdatedUtc = DateTime.UtcNow;
                plugin.SaveConfiguration();
            }

            _logger.LogInformation("Loaded {Count} English dubbed titles", parsed.Count);
            return true;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private static FrozenSet<int> Parse(byte[] json)
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

    private void LoadFromDisk()
    {
        if (!File.Exists(_cacheFilePath))
        {
            return;
        }

        try
        {
            _englishDubs = Parse(File.ReadAllBytes(_cacheFilePath));
            _logger.LogInformation("Loaded {Count} English dubbed titles from cache", _englishDubs.Count);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Could not read cached dub data, it will be re-downloaded");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _updateLock.Dispose();
    }
}
