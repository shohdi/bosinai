namespace Bonsai.Chat;

// This app has no accounts. Keep its history and inference API restricted to the
// local browser, including protection against cross-site requests and DNS rebinding.
public sealed class LocalBrowserMiddleware(RequestDelegate next)
{
    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "[::1]" or "::1";

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var rawHost = request.Headers.Host.ToString();
        if (!Uri.TryCreate(request.Scheme + "://" + rawHost, UriKind.Absolute, out var address)
            || !IsLoopbackHost(address.Host) || address.UserInfo.Length > 0
            || !rawHost.Equals(address.Authority, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid host header." });
            return;
        }

        var origin = request.Headers.Origin.ToString();
        if (request.Headers["Sec-Fetch-Site"] == "cross-site" || (origin.Length > 0 &&
            (!Uri.TryCreate(origin, UriKind.Absolute, out var source)
             || source.Scheme != address.Scheme || source.Authority != address.Authority
             || source.UserInfo.Length > 0 || source.AbsolutePath != "/"
             || source.Query.Length > 0 || source.Fragment.Length > 0)))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Open this app directly from its local address." });
            return;
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        if (request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/v1"))
            context.Response.Headers.CacheControl = "no-store";
        await next(context);
    }
}
