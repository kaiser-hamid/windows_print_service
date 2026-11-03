using System.Security.Cryptography;
using System.Text;

namespace EpicErp.PrintService.Security;

/// <summary>Requires the X-Api-Key header to match <see cref="ServiceConstants.ApiKey"/>.</summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
{
    public const string HeaderName = "X-Api-Key";

    private static readonly byte[] ExpectedKey = Encoding.UTF8.GetBytes(ServiceConstants.ApiKey);

    public async Task InvokeAsync(HttpContext context)
    {
        // CORS preflight never carries the key. The CORS middleware normally answers it before we get here.
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var providedKey = Encoding.UTF8.GetBytes(context.Request.Headers[HeaderName].ToString());
        if (!CryptographicOperations.FixedTimeEquals(providedKey, ExpectedKey))
        {
            logger.LogWarning("Rejected request to {Path}: missing or invalid API key", context.Request.Path);
            await Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Unauthorized",
                    detail: $"A valid {HeaderName} header is required.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
