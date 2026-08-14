using System.Globalization;
using System.Text.RegularExpressions;
using FluentValidation;
using Guild.Domain.Aggregates;

namespace Guild.Domain.Validators;

/// <summary>Shape rules for a role, in the style of <see cref="ChannelValidator"/>.</summary>
public class RoleValidator : AbstractValidator<Role>
{
    /// <summary>Discord's cap, adopted verbatim.</summary>
    public const int MaxRolesPerGuild = 250;

    /// <summary>Discord's role name limit.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Echo-specific: Discord has no role description at all.</summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>Echo-specific.</summary>
    public const int MaxIconUrlLength = 512;

    /// <summary>The widest sensible single emoji.</summary>
    public const int MaxUnicodeEmojiLength = 32;

    /// <summary>Three- and six-digit hex, with the leading hash.</summary>
    private static readonly Regex HexColor =
        new("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public RoleValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Role name cannot be empty.")
            .MaximumLength(MaxNameLength)
            .WithMessage($"Role name cannot exceed {MaxNameLength} characters.");

        RuleFor(x => x.Name)
            .Must(v => !v.Any(char.IsControl))
            .WithMessage("Role name cannot contain control characters.")
            .When(x => !string.IsNullOrEmpty(x.Name));

        RuleFor(x => x.Description)
            .MaximumLength(MaxDescriptionLength)
            .WithMessage($"Role description cannot exceed {MaxDescriptionLength} characters.");

        RuleFor(x => x.Color)
            .Must(v => v is not null && HexColor.IsMatch(v))
            .WithMessage("Role color must be a hex colour such as #4F8A6B.");

        RuleFor(x => x.Position)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Role position cannot be negative.");

        RuleFor(x => x.IconUrl)
            .MaximumLength(MaxIconUrlLength)
            .WithMessage($"Role icon URL cannot exceed {MaxIconUrlLength} characters.")
            .Must(v => Uri.TryCreate(v, UriKind.Absolute, out var uri)
                       && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .WithMessage("Role icon URL must be an absolute http or https URL.")
            .When(x => !string.IsNullOrWhiteSpace(x.IconUrl));

        RuleFor(x => x.UnicodeEmoji)
            .MaximumLength(MaxUnicodeEmojiLength)
            .WithMessage($"Role emoji cannot exceed {MaxUnicodeEmojiLength} characters.")
            .Must(BeASingleGrapheme)
            .WithMessage("Role emoji must be a single emoji.")
            .When(x => !string.IsNullOrWhiteSpace(x.UnicodeEmoji));
    }

    /// <summary>
    /// One user-perceived character, and not one the user could have typed as text.
    /// </summary>
    private static bool BeASingleGrapheme(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Length == 1 && char.IsAscii(value[0])) return false;

        var enumerator = StringInfo.GetTextElementEnumerator(value);
        if (!enumerator.MoveNext()) return false;
        return !enumerator.MoveNext();
    }
}
