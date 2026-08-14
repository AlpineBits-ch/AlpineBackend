using System.Linq.Expressions;
using Guild.Contracts.Bus.Response;
using RoleAggregate = Guild.Domain.Aggregates.Role;

namespace Guild.Application.Bus.Events.Role;

/// <summary>
/// Turns a role row into the <see cref="RoleSnapshot"/> the bot gateway consumes.
/// </summary>
internal static class RoleSnapshotMapper
{
    /// <summary>The EF-translatable form, for projecting straight out of a query.</summary>
    public static readonly Expression<Func<RoleAggregate, RoleSnapshot>> Projection = r => new RoleSnapshot
    {
        Id = r.Id,
        Name = r.Name,
        Color = r.Color,
        Position = r.Position,
        Permissions = (ulong)r.Permissions,
        Hoist = r.Hoist,
        Mentionable = r.Mentionable,
        Managed = r.IsManaged,
        IconUrl = r.IconUrl,
        UnicodeEmoji = r.UnicodeEmoji,
        BotUserId = r.BotUserId,
        IntegrationId = r.IntegrationId,
    };

    private static readonly Func<RoleAggregate, RoleSnapshot> Compiled = Projection.Compile();

    /// <summary>The in-memory form, for a role the caller already holds.</summary>
    public static RoleSnapshot From(RoleAggregate role) => Compiled(role);
}
