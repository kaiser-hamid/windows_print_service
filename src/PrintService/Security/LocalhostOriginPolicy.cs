namespace EpicErp.PrintService.Security;

/// <summary>
/// Browser origins allowed to call the API: http/https on localhost, 127.0.0.1 or [::1], any port.
/// </summary>
public static class LocalhostOriginPolicy
{
    public static bool IsAllowed(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || uri.Host == "127.0.0.1"
               || uri.Host == "[::1]";
    }
}
