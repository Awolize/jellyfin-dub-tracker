using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// Installs <see cref="WebUiInjectionMiddleware"/> into the Jellyfin Web pipeline.
/// </summary>
public sealed class WebUiInjectionStartupFilter : IStartupFilter
{
    private readonly ILogger<WebUiInjectionStartupFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebUiInjectionStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{WebUiInjectionStartupFilter}"/> interface.</param>
    public WebUiInjectionStartupFilter(ILogger<WebUiInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            _logger.LogInformation(
                "Anime Dub Status {Version} is installing its Jellyfin Web UI injection middleware",
                typeof(WebUiInjectionStartupFilter).Assembly.GetName().Version);

            app.UseMiddleware<WebUiInjectionMiddleware>();
            next(app);
        };
    }
}
