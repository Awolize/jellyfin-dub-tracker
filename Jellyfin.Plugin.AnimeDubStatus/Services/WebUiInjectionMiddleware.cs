using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeDubStatus.Services;

/// <summary>
/// Serves the plugin's web assets and injects the badge script into Jellyfin Web.
/// </summary>
/// <remarks>
/// Jellyfin offers no supported hook for adding JavaScript to the web client, so the
/// response for <c>index.html</c> is rewritten on its way out. Doing this in middleware
/// rather than on disk keeps the plugin independent of where Jellyfin Web is installed.
/// </remarks>
public sealed class WebUiInjectionMiddleware
{
    private const string ScriptResourceName = "Jellyfin.Plugin.AnimeDubStatus.Web.badge.js";
    private const string IndexFileName = "index.html";
    private const string WebRoot = "/web";
    private const string WebRootPrefix = "/web/";

    private static readonly Lazy<byte[]> ScriptBytes = new(LoadScript, isThreadSafe: true);
    private static readonly Lazy<string> ScriptETag = new(() => ComputeETag(ScriptBytes.Value), isThreadSafe: true);

    private readonly RequestDelegate _next;
    private readonly ILogger<WebUiInjectionMiddleware> _logger;
    private int _outcomeReported;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebUiInjectionMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{WebUiInjectionMiddleware}"/> interface.</param>
    public WebUiInjectionMiddleware(RequestDelegate next, ILogger<WebUiInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="index">Instance of the <see cref="DubStatusIndex"/> class.</param>
    /// <param name="tracks">Instance of the <see cref="EpisodeTrackIndex"/> class.</param>
    /// <returns>A task that completes when the response is written.</returns>
    public async Task InvokeAsync(HttpContext context, DubStatusIndex index, EpisodeTrackIndex tracks)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var marker = path.IndexOf(WebRootPrefix, StringComparison.OrdinalIgnoreCase);
        if (marker < 0 && !path.EndsWith(WebRoot, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var relative = marker >= 0
            ? path[(marker + WebRootPrefix.Length)..]
            : string.Empty;

        if (relative.Length == 0)
        {
            relative = IndexFileName;
        }

        if (relative.Equals(WebTransformation.ScriptFileName, StringComparison.OrdinalIgnoreCase))
        {
            await WriteScriptAsync(context).ConfigureAwait(false);
            return;
        }

        if (relative.Equals(WebTransformation.DataFileName, StringComparison.OrdinalIgnoreCase))
        {
            await WriteDataAsync(context, index).ConfigureAwait(false);
            return;
        }

        if (relative.Equals(WebTransformation.TracksFileName, StringComparison.OrdinalIgnoreCase))
        {
            await WriteTracksAsync(context, tracks).ConfigureAwait(false);
            return;
        }

        if (relative.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase))
        {
            await InjectIndexAsync(context).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static byte[] LoadScript()
    {
        using var stream = typeof(WebUiInjectionMiddleware).Assembly
            .GetManifestResourceStream(ScriptResourceName);

        if (stream is null)
        {
            return [];
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ComputeETag(byte[] payload)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(payload);
        return string.Concat("\"", Convert.ToHexString(hash, 0, 8).ToLowerInvariant(), "\"");
    }

    private static bool IsNotModified(HttpContext context, string etag)
    {
        return string.Equals(
            context.Request.Headers.IfNoneMatch.ToString(),
            etag,
            StringComparison.Ordinal);
    }

    private static async Task WriteScriptAsync(HttpContext context)
    {
        var bytes = ScriptBytes.Value;
        if (bytes.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var etag = ScriptETag.Value;
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";

        if (IsNotModified(context, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/javascript; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteTracksAsync(HttpContext context, EpisodeTrackIndex tracks)
    {
        // Served here rather than from the controller because the badge script fetches
        // anonymously and must not depend on an API token.
        if (!Guid.TryParse(context.Request.Query["series"].ToString(), out var seriesId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("A series query parameter is required.").ConfigureAwait(false);
            return;
        }

        var (payload, etag) = tracks.GetSnapshot(seriesId);
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";

        if (IsNotModified(context, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteDataAsync(HttpContext context, DubStatusIndex index)
    {
        var (payload, etag, _) = index.GetSnapshot();
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";

        if (IsNotModified(context, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
    }

    private async Task InjectIndexAsync(HttpContext context)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        // The document is rewritten per request, so upstream conditional requests are
        // dropped: a 304 from them would skip the rewrite and leave a cached,
        // badge-less document in the browser. Our own entity tag decides freshness.
        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();

        try
        {
            context.Request.Headers.Remove("If-None-Match");
            context.Request.Headers.Remove("If-Modified-Since");

            // Ask for the document uncompressed so it can be rewritten as text.
            context.Request.Headers.Remove("Accept-Encoding");
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var upstream = buffer.ToArray();
        if (context.Response.StatusCode != StatusCodes.Status200OK || upstream.Length == 0)
        {
            await originalBody.WriteAsync(upstream).ConfigureAwait(false);
            return;
        }

        var answer = upstream;
        try
        {
            string html;
            using (var reader = new StreamReader(
                new MemoryStream(upstream),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true))
            {
                html = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var injected = WebTransformation.InjectScript(html);
            if (!string.Equals(injected, html, StringComparison.Ordinal))
            {
                answer = Encoding.UTF8.GetBytes(injected);
                ReportOutcome(injected: true, exception: null);
            }
            else
            {
                ReportOutcome(injected: false, exception: null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportOutcome(injected: false, exception: ex);
            answer = upstream;
        }

        var etag = ComputeETag(answer);
        context.Response.Headers.Remove("Content-Encoding");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";

        if (string.Equals(ifNoneMatch, etag, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.ContentLength = 0;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = answer.Length;
        await originalBody.WriteAsync(answer).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the first injection attempt, so a Jellyfin upgrade that breaks the
    /// rewrite is visible in the log rather than only in a missing badge.
    /// </summary>
    /// <param name="injected">Whether the document was rewritten.</param>
    /// <param name="exception">The failure, when the rewrite threw.</param>
    private void ReportOutcome(bool injected, Exception? exception)
    {
        if (Interlocked.Exchange(ref _outcomeReported, 1) != 0)
        {
            return;
        }

        if (exception is not null)
        {
            _logger.LogWarning(
                exception,
                "Anime Dub Status failed to rewrite index.html and served it unchanged; the badge will not appear");
        }
        else if (injected)
        {
            _logger.LogInformation(
                "Anime Dub Status is injecting its badge script into Jellyfin Web");
        }
        else
        {
            _logger.LogWarning(
                "Anime Dub Status could not inject its badge script: index.html has no closing body tag. The badge will not appear until this is fixed.");
        }
    }
}
