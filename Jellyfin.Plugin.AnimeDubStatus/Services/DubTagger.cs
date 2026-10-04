using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// Adds or removes the English dub tag on series in the library.
/// </summary>
public sealed class DubTagger
{
    /// <summary>
    /// The tag applied to series with an English dub.
    /// </summary>
    public const string TagName = "English Dub Available";

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
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true
        });

        var added = 0;
        var removed = 0;

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            var shouldHave = IsEnglishDubbed(item);
            var tags = item.Tags ?? [];
            var has = tags.Contains(TagName, StringComparer.OrdinalIgnoreCase);

            if (shouldHave != has)
            {
                item.Tags = shouldHave
                    ? [.. tags, TagName]
                    : tags.Where(t => !string.Equals(t, TagName, StringComparison.OrdinalIgnoreCase)).ToArray();

                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                if (shouldHave)
                {
                    added++;
                }
                else
                {
                    removed++;
                }
            }

            progress.Report(100.0 * (i + 1) / items.Count);
        }

        _logger.LogInformation("Checked {Total} series: tagged {Added}, untagged {Removed}", items.Count, added, removed);
    }

    private bool IsEnglishDubbed(BaseItem item)
    {
        if (item.TryGetProviderId("MyAnimeList", out var malText)
            && int.TryParse(malText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var malId))
        {
            return _dubData.IsEnglishDubbed(malId);
        }

        if (item.TryGetProviderId("AniList", out var anilistText)
            && int.TryParse(anilistText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var anilistId)
            && _dubData.TryGetMalId(anilistId, out var mappedMalId))
        {
            return _dubData.IsEnglishDubbed(mappedMalId);
        }

        return false;
    }
}
