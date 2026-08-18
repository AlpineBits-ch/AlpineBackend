namespace Guild.Application.Dtos.Response;

/// <summary>
/// Why a publish was refused, in a code a client can branch on. Named rather than anonymous so it
/// reaches the generated OpenAPI docs: a caller that has to match on prose has no contract at all.
/// </summary>
public class WikiPublicationErrorDto
{
    /// <summary>The refusal, as a stable code. See <see cref="WikiPublicationErrors"/>.</summary>
    public required string Error { get; set; }

    /// <summary>The same thing in English, for a client with nothing better to show.</summary>
    public required string Message { get; set; }
}

/// <summary>The codes <see cref="WikiPublicationErrorDto.Error"/> takes.</summary>
public static class WikiPublicationErrors
{
    /// <summary>A household may not publish at all.</summary>
    public const string NotAvailable = "wiki_publication_not_available";

    /// <summary>The page is private inside the guild, so it cannot also be on the open internet.</summary>
    public const string PagePrivate = "wiki_page_private";

    /// <summary>The page's cover is hosted somewhere this instance does not control, which would
    /// beacon every anonymous visitor to an address its author chose.</summary>
    public const string CoverNotHosted = "wiki_page_cover_not_hosted";

    /// <summary>Another guild already answers on that address.</summary>
    public const string SlugTaken = "wiki_slug_taken";
}
