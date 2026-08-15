namespace AppEnvironment;

/// <summary>
/// The hostnames that mean "this instance" when one of them turns up in a link somebody posted.
/// </summary>
public static class InstanceLinkHosts
{
    /// <summary>Comma- or space-separated extra hostnames.</summary>
    public const string EnvironmentVariable = "INSTANCE_LINK_HOSTS";

    /// <summary>
    /// Every authority this instance answers to: a bare hostname where the configured URL used the
    /// scheme's default port, and <c>host:port</c> where it named one explicitly.
    /// </summary>
    public static IReadOnlyCollection<string> All
    {
        get
        {
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Add(hosts, Env.GeneralConfiguration.InstanceUrl);
            Add(hosts, WebClientHost.BaseUrl);

            var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                foreach (var entry in configured.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    Add(hosts, entry);
            }

            return hosts;
        }
    }

    /// <summary>Whether a URI points at this instance.</summary>
    public static bool IsInstanceHost(Uri? uri) =>
        uri is not null && Matches(uri, All);

    /// <summary>The match, kept next to the set that feeds it.</summary>
    public static bool Matches(Uri uri, IReadOnlyCollection<string> authorities) =>
        authorities.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
        || authorities.Contains($"{uri.Host}:{uri.Port}", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Accepts either a full URL or a bare hostname, because the variables feeding this are written
    /// by operators in both forms - <c>INSTANCE_URL</c> is a URL, <c>APP_DOMAIN</c> and friends are
    /// hostnames, and <c>INSTANCE_LINK_HOSTS</c> sits between them.
    /// </summary>
    private static void Add(HashSet<string> hosts, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var trimmed = value.Trim().TrimEnd('/');

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            hosts.Add(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}");
            return;
        }

        // A bare hostname, optionally with a port.
        var authority = trimmed.Split('/')[0];
        if (authority.Length > 0) hosts.Add(authority);
    }
}
