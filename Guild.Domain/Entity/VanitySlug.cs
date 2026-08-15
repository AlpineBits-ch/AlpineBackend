using System.Text.RegularExpressions;

namespace Guild.Domain.Entity;

/// <summary>The grammar of a guild's vanity invite slug, and the words nobody may take.</summary>
public static partial class VanitySlug
{
    public const int MinLength = 3;
    public const int MaxLength = 32;

    /// <summary>Lowercase letters, digits and single interior hyphens.</summary>
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Grammar();

    /// <summary>Routes that already exist on the gateway or the landing site, plus the words a
    /// phishing link would want. Compared after normalization, so casing cannot slip one
    /// through.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "about", "account", "accounts", "admin", "administrator", "api", "app", "apps",
        "auth", "billing", "blog", "bot", "bots", "brand", "careers", "cdn", "channels",
        "checkout", "contact", "cookies", "dashboard", "developer", "developers", "discover",
        "docs", "download", "downloads", "echo", "faq", "feedback", "forgot", "gift", "gifts",
        "guild", "guilds", "help", "home", "identity", "images", "invite", "invites", "jobs",
        "legal", "login", "logout", "mail", "media", "messages", "moderation", "new", "news",
        "nitro", "official", "partner", "partners", "password", "pay", "payment", "payments",
        "premium", "press", "pricing", "privacy", "profile", "register", "reset", "root",
        "safety", "security", "settings", "signin", "signup", "staff", "static", "status",
        "store", "support", "system", "terms", "test", "tos", "upgrade", "user", "users",
        "venta", "verify", "webhook", "webhooks", "www",
    };

    /// <summary>The single spelling that reaches the column.</summary>
    public static string Normalize(string value) => value.Trim().ToLowerInvariant();

    /// <summary>Why a slug was refused, or null if it was not.</summary>
    public static string? Validate(string normalized)
    {
        if (normalized.Length < MinLength)
            return $"A vanity URL must be at least {MinLength} characters.";
        if (normalized.Length > MaxLength)
            return $"A vanity URL may be at most {MaxLength} characters.";
        if (!Grammar().IsMatch(normalized))
            return "A vanity URL may contain only lowercase letters, digits and single hyphens between them.";
        if (Reserved.Contains(normalized))
            return "That vanity URL is reserved.";

        return null;
    }

    public static bool IsReserved(string normalized) => Reserved.Contains(normalized);
}
