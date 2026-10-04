using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AnimeDubStatus.Api;

/// <summary>
/// Serves the web client script that draws dub badges.
/// </summary>
[ApiController]
[Route("AnimeDubStatus")]
public class BadgeController : ControllerBase
{
    private const string ResourceName = "Jellyfin.Plugin.AnimeDubStatus.Web.badge.js";

    /// <summary>
    /// Gets the badge script.
    /// </summary>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("badge.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetScript()
    {
        var stream = typeof(BadgeController).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, "application/javascript");
    }
}
