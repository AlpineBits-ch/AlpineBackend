using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(GuildAuditLogEntry), nameof(GuildAuditLogEntry.Guild))]
public partial class AuditLogEntryDto
{
}
