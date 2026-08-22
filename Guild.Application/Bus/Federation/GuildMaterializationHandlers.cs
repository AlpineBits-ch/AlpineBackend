using Federation.Contracts.Materialization.Guild;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Federation;

/// <summary>
/// Materializes remote guild-membership federation events as shadow GuildMember rows, flagged via
/// the existing (previously unused) GuildMember.FederatedServerId field.
/// </summary>
public class GuildMaterializationHandler
{
    public static Task Handle(FederatedGuildMemberJoinedReceived message, MicroserviceContext db, CancellationToken ct) =>
        AddShadowMemberIfMissingAsync(db, message.GuildId, message.UserId, message.OriginInstanceId, ct);

    public static Task Handle(FederatedGuildInviteRedeemedReceived message, MicroserviceContext db, CancellationToken ct) =>
        AddShadowMemberIfMissingAsync(db, message.GuildId, Strip(message.SenderId), message.OriginInstanceId, ct);

    public static async Task Handle(FederatedGuildMemberLeftReceived message, MicroserviceContext db, CancellationToken ct)
    {
        var member = await db.GuildMembers.FirstOrDefaultAsync(
            m => m.GuildId == message.GuildId && m.UserId == message.UserId, ct);
        if (member is null) return;

        db.GuildMembers.Remove(member);
        await db.SaveChangesAsync(ct);
    }

    public static async Task Handle(FederatedGuildMemberBannedReceived message, MicroserviceContext db, CancellationToken ct)
    {
        var member = await db.GuildMembers.FirstOrDefaultAsync(
            m => m.GuildId == message.GuildId && m.UserId == message.BannedUserId, ct);
        if (member is not null)
            db.GuildMembers.Remove(member);

        var alreadyBanned = await db.GuildBans.AnyAsync(
            b => b.GuildId == message.GuildId && b.BannedUserId == message.BannedUserId, ct);
        if (!alreadyBanned)
        {
            db.GuildBans.Add(GuildBan.Create(new CreateGuildBanParams
            {
                GuildId = message.GuildId,
                BannedUserId = message.BannedUserId,
                BannedByUserId = message.SenderId,
                Reason = message.Reason,
            }));
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task AddShadowMemberIfMissingAsync(
        MicroserviceContext db, string guildId, string userId, string originInstanceId, CancellationToken ct)
    {
        var exists = await db.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
        if (exists) return;

        // No display name is available in-band on these federation events - the federated user
        // id is used as a placeholder until profile enrichment (IFederationProvider already
        // exposes GetUserProfileAsync) is wired into materialization too.
        var member = GuildMember.CreateForUser(new CreateGuildMemberParams
        {
            GuildId = guildId,
            UserId = userId,
            Username = userId,
        });
        member.FederatedServerId = originInstanceId;

        db.GuildMembers.Add(member);
        await db.SaveChangesAsync(ct);
    }

    private static string Strip(string federatedOrPlainId)
    {
        var colonIndex = federatedOrPlainId.IndexOf(':');
        return colonIndex < 0 ? federatedOrPlainId : federatedOrPlainId[..colonIndex];
    }
}
