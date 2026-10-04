using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeDubStatus.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AnimeDubStatus.Tasks;

/// <summary>
/// Scheduled task that refreshes the English dub dataset.
/// </summary>
public class UpdateDubDataTask : IScheduledTask
{
    private readonly DubDataService _dubData;
    private readonly DubTagger _tagger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateDubDataTask"/> class.
    /// </summary>
    /// <param name="dubData">Instance of the <see cref="DubDataService"/> class.</param>
    /// <param name="tagger">Instance of the <see cref="DubTagger"/> class.</param>
    public UpdateDubDataTask(DubDataService dubData, DubTagger tagger)
    {
        _dubData = dubData;
        _tagger = tagger;
    }

    /// <inheritdoc />
    public string Name => "Update English dub data";

    /// <inheritdoc />
    public string Key => "AnimeDubStatusUpdateData";

    /// <inheritdoc />
    public string Description => "Downloads the latest English dub list from MyDubList.";

    /// <inheritdoc />
    public string Category => "Anime Dub Status";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        await _dubData.UpdateAsync(cancellationToken).ConfigureAwait(false);
        await _tagger.ApplyAsync(new Progress<double>(p => progress.Report(p)), cancellationToken).ConfigureAwait(false);
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
