namespace Guild.Domain.Enums;

/// <summary>What a guild *is*, as picked once at creation. Purely a preset and a presentation
/// hint - it seeds <see cref="GuildFeatures"/> and tells clients which shell to render
/// ("House" vs "Server" vs "Team", which settings pages exist, whether it's discoverable).
/// Nothing in this service gates behaviour on Kind directly; gating is always on Features, so
/// an owner can turn an individual module on without having to re-type the whole guild.
///
/// Community is deliberately the zero value: every row that existed before this enum landed
/// migrates to it, which is also the preset that matches how those guilds already behaved.
/// Appended-only, like ChannelType - Npgsql maps it by name and appending is the only change
/// Postgres can make to an existing enum type without a rewrite.</summary>
public enum GuildKind
{
    Community,
    Household,
    Team,
    Study,
    Event,
}
