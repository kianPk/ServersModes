namespace ServersModes.Shared;

// WEB_DOMAIN is set on every dedicated server's container; one created before
// it only has API_DOMAIN, whose "api." host leads to the same site.
public static class SiteHost
{
    public static string? Current()
    {
        if (Host(Environment.GetEnvironmentVariable("WEB_DOMAIN")) is { } web)
        {
            return web;
        }

        return Host(Environment.GetEnvironmentVariable("API_DOMAIN")) is { } api && api.StartsWith("api.")
            ? api["api.".Length..]
            : null;
    }

    // Clan tags are short; keep the bare host (yguard.ir).
    public static string? ClanTag()
    {
        var host = Current();
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        return host.Length <= 16 ? host : host[..16];
    }

    private static string? Host(string? value)
    {
        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return Uri.TryCreate(value.Contains("://") ? value : $"https://{value}", UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }
}
