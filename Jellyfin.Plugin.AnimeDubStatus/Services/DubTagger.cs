using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AnimeDubStatus.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// Adds or removes the tracked language's dub tag on series in the library.
/// </summary>
public sealed class DubTagger
{
    /// <summary>
    /// The tag older versions applied, removed when the tracked language changes so it
    /// does not linger after an upgrade.
    /// </summary>
    private const string LegacyTagName = "English Dub Available";

    private readonly ILibraryManager _libraryManager;
    private readonly DubDataService _dubData;
    private readonly ILogger<DubTagger> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DubTagger"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="dubData">Instance of the <see cref="DubDataService"/> class.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{DubTagger}"/> interface.</param>
    public DubTagger(ILibraryManager libraryManager, DubDataService dubData, ILogger<DubTagger> logger)
    {
        _libraryManager = libraryManager;
        _dubData = dubData;
        _logger = logger;
    }

    /// <summary>
    /// Tags every series according to the current dub data, removing stale tags.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when tagging is done.</returns>
    public async Task ApplyAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = TrackSettings.Current;
        var tagName = TrackSettings.GetTagName(configuration);
        var staleTags = GetStaleTags(configuration, tagName);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true
        });

        var added = 0;
        var removed = 0;
        var cleaned = 0;

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            var tags = item.Tags ?? [];

            // Drop tags left behind by a previously tracked language in the same pass.
            var current = tags
                .Where(tag => !staleTags.Contains(tag))
                .ToArray();

            var shouldHave = IsDubbed(item);
            var has = current.Contains(tagName, StringComparer.OrdinalIgnoreCase);

            var updated = shouldHave
                ? has ? current : [.. current, tagName]
                : current;

            if (!SameTags(tags, updated))
            {
                item.Tags = updated;
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                if (shouldHave && !has)
                {
                    added++;
                }
                else if (!shouldHave && tags.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                {
                    removed++;
                }
                else
                {
                    cleaned++;
                }
            }

            progress.Report(100.0 * (i + 1) / items.Count);
        }

        if (!string.Equals(configuration.AppliedTagName, tagName, StringComparison.Ordinal))
        {
            configuration.AppliedTagName = tagName;
            Plugin.Instance?.SaveConfiguration();
        }

        _logger.LogInformation(
            "Checked {Total} series for {Tag}: tagged {Added}, untagged {Removed}, cleaned up {Cleaned}",
            items.Count,
            tagName,
            added,
            removed,
            cleaned);
    }

    private static bool SameTags(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the tags that must not survive a change of tracked language.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <param name="tagName">The tag being applied now.</param>
    /// <returns>The tags to strip.</returns>
    private static HashSet<string> GetStaleTags(PluginConfiguration configuration, string tagName)
    {
        var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LegacyTagName
        };

        if (!string.IsNullOrEmpty(configuration.AppliedTagName))
        {
            stale.Add(configuration.AppliedTagName);
        }

        // The tag in use is never stale.
        stale.Remove(tagName);
        return stale;
    }

    private bool IsDubbed(BaseItem item)
    {
        if (item.TryGetProviderId("MyAnimeList", out var malText)
            && int.TryParse(malText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var malId))
        {
            return _dubData.IsDubbed(malId);
        }

        if (item.TryGetProviderId("AniList", out var anilistText)
            && int.TryParse(anilistText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var anilistId)
            && _dubData.TryGetMalId(anilistId, out var mappedMalId))
        {
            return _dubData.IsDubbed(mappedMalId);
        }

        return false;
    }
}
