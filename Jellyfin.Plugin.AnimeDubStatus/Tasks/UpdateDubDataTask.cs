using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeDubStatus.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AnimeDubStatus.Tasks;

/// <summary>
/// Scheduled task that refreshes the dub dataset and re-tags the library.
/// </summary>
public class UpdateDubDataTask : IScheduledTask
{
    private readonly DubDataService _dubData;
    private readonly DubTagger _tagger;
    private readonly DubStatusIndex _index;
    private readonly EpisodeTrackIndex _tracks;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateDubDataTask"/> class.
    /// </summary>
    /// <param name="dubData">Instance of the <see cref="DubDataService"/> class.</param>
    /// <param name="tagger">Instance of the <see cref="DubTagger"/> class.</param>
    /// <param name="index">Instance of the <see cref="DubStatusIndex"/> class.</param>
    /// <param name="tracks">Instance of the <see cref="EpisodeTrackIndex"/> class.</param>
    public UpdateDubDataTask(
        DubDataService dubData,
        DubTagger tagger,
        DubStatusIndex index,
        EpisodeTrackIndex tracks)
    {
        _dubData = dubData;
        _tagger = tagger;
        _index = index;
        _tracks = tracks;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately language-neutral: the scheduler captures task text when the plugin
    /// loads, so naming the configured language here would go stale until a restart.
    /// </remarks>
    public string Name => "Update dub data";

    /// <inheritdoc />
    public string Key => "AnimeDubStatusUpdateData";

    /// <inheritdoc />
    public string Description => "Downloads the latest dub list for the tracked language and re-tags the library.";

    /// <inheritdoc />
    public string Category => "Anime Dub Status";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        await _dubData.UpdateAsync(cancellationToken).ConfigureAwait(false);

        // Tags are written item by item, so mark the badge snapshot stale as the run
        // proceeds. Invalidating only sets a flag and the query happens lazily on the
        // next request, so this lets badges appear as the re-tag advances instead of
        // jumping once at the end.
        var tagProgress = new Progress<double>(value =>
        {
            progress.Report(value);
            _index.Invalidate();
        });

        await _tagger.ApplyAsync(tagProgress, cancellationToken).ConfigureAwait(false);

        // Tags changed, so the web client's list of dubbed series is stale.
        _index.Invalidate();

        // Now that the tags have settled, measure how much of each tagged series the
        // library holds. This reads every episode's media streams, so it belongs here
        // rather than in a request.
        _tracks.RefreshLibrary(_index.GetTaggedSeriesIds(), cancellationToken);

        // Publish the measurements alongside the tag list.
        _index.Invalidate();

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };

        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }
}
