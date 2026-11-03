using Microsoft.Net.Http.Headers;

namespace EpicErp.PrintService.Security;

/// <summary>
/// Rejects any request whose Origin header is present but not localhost. CORS on its own only stops
/// the browser from reading the response; this stops a foreign page's request from doing anything.
/// Requests without an Origin header (curl, native apps) pass through and still need the API key.
/// </summary>
public sealed class OriginGuardMiddleware(RequestDelegate next, ILogger<OriginGuardMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderNames.Origin, out var origin) && !LocalhostOriginPolicy.IsAllowed(origin))
        {
            logger.LogWarning("Rejected request from disallowed origin {Origin} to {Path}", origin.ToString(), context.Request.Path);
            await Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Origin not allowed",
                    detail: "This print service only accepts browser requests from localhost origins.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
