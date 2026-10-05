namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// The aggregate coverage of a set of episodes.
/// </summary>
/// <param name="Released">How many episodes count, that is, have aired.</param>
/// <param name="Present">How many of those carry the track.</param>
/// <param name="Percent">Present as a rounded percentage of released.</param>
public sealed record TrackCoverageResult(int Released, int Present, int Percent);
