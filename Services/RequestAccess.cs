using Watchboard.Api.Models;

namespace Watchboard.Api.Services;

public static class RequestAccess
{
    public static bool IsValidOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        Uri.CheckHostName(uri.Host) != UriHostNameType.Unknown &&
        string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedOrigin(string origin, WatchboardOptions options)
    {
        // Non-browser clients still need the write header and authenticated session.
        if (origin.Length == 0) return true;
        if (!IsValidOrigin(origin)) return false;
        if (options.AllowedOrigins.Length > 0)
            return options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);

        var uri = new Uri(origin);
        return !options.AllowRemoteAccess && uri.Scheme == "http" &&
            uri.Host is "127.0.0.1" or "localhost" or "[::1]" && uri.Port is 5333 or 5196 or 5197;
    }
}
