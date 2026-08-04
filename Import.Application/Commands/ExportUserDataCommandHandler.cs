using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Import.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Import.Application.Commands;

/// <summary>
/// Import's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling of
/// <see cref="PurgeUserDataCommandHandler"/>.
///
/// <para>Unlike the purge, which is a genuine no-op here, this one has something to say.
/// <c>ImportJob.RequestedByUserId</c> is attribution-only and so is left pointing at the tombstone
/// when an account is deleted - but "you started a Discord import of this server on this date" is a
/// record of something the subject did, and an access request is entitled to it even where an erasure
/// request would leave it alone. Disclosure and erasure are different questions about the same row.</para>
///
/// <para><c>GuildLink</c> and <c>ImportEntityMapping</c> carry no user reference at all and are not
/// exported.</para>
/// </summary>
public class ExportUserDataCommandHandler
{
    public static async Task<ExportUserDataResponse> Handle(
        ExportUserDataCommand command, MicroserviceContext ctx)
    {
        var jobs = await ctx.ImportJobs
            .AsNoTracking()
            .Where(j => j.RequestedByUserId == command.UserId)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync();

        var fragment = new
        {
            importJobs = jobs.Select(j => new
            {
                j.Id,
                j.EchoGuildId,
                j.DiscordGuildId,
                status = j.Status.ToString(),
                j.ErrorMessage,
                j.CreatedAt,
                j.CompletedAt,
            }),
        };

        return new ExportUserDataResponse
        {
            ExportId = command.ExportId,
            UserId = command.UserId,
            Service = "import",
            FragmentJson = JsonSerializer.Serialize(fragment, UserDataExportJson.Options),
            RowCounts = new Dictionary<string, int> { ["importJobs"] = jobs.Count },
        };
    }
}
