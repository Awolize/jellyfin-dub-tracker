using Jellyfin.Plugin.AnimeDubStatus.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AnimeDubStatus;

/// <summary>
/// Registers the plugin's services with Jellyfin's dependency injection.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(
    IServiceCollection serviceCollection,
    IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<DubDataService>();
        serviceCollection.AddSingleton<DubStatusIndex>();
        serviceCollection.AddSingleton<EpisodeTrackIndex>();
        serviceCollection.AddSingleton<DubTagger>();
        serviceCollection.AddTransient<IStartupFilter, WebUiInjectionStartupFilter>();
    }
}
