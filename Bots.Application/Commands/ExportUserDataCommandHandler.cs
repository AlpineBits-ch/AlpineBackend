using System.Text.Json;
using Bots.Infrastructure.Persistence;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;

namespace Bots.Application.Commands;

/// <summary>
/// Bots' participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling of
/// <see cref="PurgeUserDataCommandHandler"/>.
///
/// <para>What the subject owns here is the applications they registered: names, descriptions,
/// permissions, the commands they defined and where they installed them. Those are things the subject
/// created, and a developer asking what this platform holds about them expects their applications to
/// be in the answer.</para>
///
/// <para><b>The client secret is not exported, and its absence is the point.</b> An application's
/// secret is a live credential, not information about its owner - the owner already chose it or was
/// given it, and can rotate it through the API. Putting it in a zip that will be downloaded, synced
/// and left in a downloads folder would turn an access request into a credential-disclosure path. The
/// same rule the Identity fragment applies to password hashes and master keys.</para>
///
/// <para><b>Installations name guilds, never guild members.</b> The subject may see which servers
/// their own bot is installed in; the membership of those servers is not theirs.</para>
/// </summary>
public class ExportUserDataCommandHandler
{
    public static async Task<ExportUserDataResponse> Handle(
        ExportUserDataCommand command, MicroserviceContext ctx)
    {
        var applications = await ctx.BotApplications
            .AsNoTracking()
            .Where(a => a.OwnerUserId == command.UserId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        var applicationIds = applications.Select(a => a.Id).ToList();

        var installations = await ctx.BotInstallations
            .AsNoTracking()
            .Where(i => applicationIds.Contains(i.BotApplicationId))
            .ToListAsync();

        var commands = await ctx.BotCommands
            .AsNoTracking()
            .Where(c => applicationIds.Contains(c.BotApplicationId))
            .ToListAsync();

        var fragment = new
        {
            applications = applications.Select(a => new
            {
                a.Id,
                a.Name,
                a.Description,
                a.IconUrl,
                a.BotUserId,
                a.DefaultPermissions,
                a.IsEnabled,
                a.CreatedAt,
                // No client secret. See this handler's remarks.
            }),
            installations = installations.Select(i => new
            {
                i.Id,
                i.BotApplicationId,
                i.GuildId,
                i.GrantedPermissions,
                i.InstalledAt,
                // InstalledByUserId is omitted: somebody else may have installed the subject's bot
                // into their own server, and which account that was is a fact about them.
            }),
            commands = commands.Select(c => new
            {
                c.Id,
                c.BotApplicationId,
                c.Name,
                c.Description,
                c.GuildId,
                c.OptionsJson,
            }),
        };

        return new ExportUserDataResponse
        {
            ExportId = command.ExportId,
            UserId = command.UserId,
            Service = "bots",
            FragmentJson = JsonSerializer.Serialize(fragment, UserDataExportJson.Options),
            RowCounts = new Dictionary<string, int>
            {
                ["applications"] = applications.Count,
                ["installations"] = installations.Count,
                ["commands"] = commands.Count,
            },
        };
    }
}
