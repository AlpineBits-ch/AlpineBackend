using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreatePersonaAutoproxyStateParams
{
    public string UserId { get; init; } = null!;
    public string GuildId { get; init; } = null!;
    public string ChannelId { get; init; } = null!;
    public AutoproxyMode Mode { get; init; } = AutoproxyMode.Off;
    public string? PersonaId { get; init; }
}

/// <summary>
/// What one person's next unmarked message in one channel goes out as. Server-side rather than
/// client configuration on purpose: it then works identically on every client, survives a client
/// that has never heard of personas, and cannot be taken down by a third party.
/// </summary>
public class PersonaAutoproxyState : BaseEntity<PersonaAutoproxyState>, IPrefixedEntity
{
    public static string Prefix { get; } = "apxy";

    public string UserId { get; set; } = null!;

    /// <summary>Carried alongside the channel so that leaving a guild can clear the whole set in
    /// one delete, which the channel id alone cannot express.</summary>
    public string GuildId { get; set; } = null!;

    public string ChannelId { get; set; } = null!;

    public AutoproxyMode Mode { get; set; } = AutoproxyMode.Off;

    /// <summary>Pinned by the caller under <see cref="AutoproxyMode.Pinned"/> and written back by
    /// the send path under <see cref="AutoproxyMode.Sticky"/>. Nulled when the persona goes away,
    /// which resolves as <see cref="AutoproxyMode.Off"/> rather than as an error.</summary>
    public string? PersonaId { get; set; }

    public static PersonaAutoproxyState Create(CreatePersonaAutoproxyStateParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new PersonaAutoproxyState
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            UserId = @params.UserId,
            GuildId = @params.GuildId,
            ChannelId = @params.ChannelId,
            Mode = @params.Mode,
            PersonaId = @params.Mode == AutoproxyMode.Off ? null : @params.PersonaId,
        };
    }

    /// <summary>The persona this state currently resolves to, or null when it resolves to nothing.</summary>
    public string? ResolvePersonaId() =>
        Mode == AutoproxyMode.Off || string.IsNullOrWhiteSpace(PersonaId) ? null : PersonaId;

    /// <summary>Repoints the state, dropping the persona when it is switched off.</summary>
    /// <param name="mode">The new mode.</param>
    /// <param name="personaId">The persona to pin, required for <see cref="AutoproxyMode.Pinned"/>
    /// and ignored otherwise.</param>
    /// <exception cref="ArgumentException">Pinned was requested with no persona.</exception>
    public void Set(AutoproxyMode mode, string? personaId)
    {
        if (mode == AutoproxyMode.Pinned && string.IsNullOrWhiteSpace(personaId))
        {
            throw new ArgumentException("Pinned autoproxy needs a persona to pin.", nameof(personaId));
        }

        Mode = mode;
        PersonaId = mode switch
        {
            AutoproxyMode.Off => null,
            AutoproxyMode.Pinned => personaId,
            // Sticky keeps whatever the send path last wrote until it writes again.
            _ => PersonaId,
        };
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
