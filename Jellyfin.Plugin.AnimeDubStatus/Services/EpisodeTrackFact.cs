namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// One episode's contribution to a coverage measurement.
/// </summary>
/// <param name="HasTrack">Whether the episode carries the tracked track.</param>
/// <param name="IsUnaired">Whether the episode has not aired yet.</param>
public sealed record EpisodeTrackFact(bool HasTrack, bool IsUnaired);
