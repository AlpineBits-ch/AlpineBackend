using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Import.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Import.Application.Commands;

/// <summary>
/// Import's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling of
/// <see cref="PurgeUserDataCommandHandler"/>.
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
