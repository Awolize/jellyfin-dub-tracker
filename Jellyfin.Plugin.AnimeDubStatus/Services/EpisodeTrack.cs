using System;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// One episode's track state.
/// </summary>
/// <param name="Id">The episode's Jellyfin identifier.</param>
/// <param name="HasTrack">Whether the episode carries the tracked track.</param>
/// <param name="IsUnaired">Whether the episode has not aired yet.</param>
/// <param name="Language">The closest stream language found, when there was one.</param>
public sealed record EpisodeTrack(Guid Id, bool HasTrack, bool IsUnaired, string? Language);
