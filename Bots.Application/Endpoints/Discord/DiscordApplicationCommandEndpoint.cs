using System.Security.Claims;
using System.Text.Json;
using Bots.Domain.Entity;
using Bots.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Bots.Application.Endpoints.Discord;

public class DiscordCommandDto
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonElement? Options { get; set; }
}

/// <summary>Discord's real application-command registration surface.</summary>
[Authorize]
public class DiscordApplicationCommandEndpoint
{
    [WolverinePut("/api/discord/v10/applications/{applicationId}/commands")]
    public Task<IResult> BulkOverwriteGlobalAsync(string applicationId, List<DiscordCommandDto> dtos,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx) =>
        BulkOverwriteAsync(applicationId, null, dtos, user, ctx);

    [WolverineGet("/api/discord/v10/applications/{applicationId}/commands")]
    public Task<IResult> ListGlobalAsync(string applicationId, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx) =>
        ListAsync(applicationId, null, user, ctx);

    [WolverinePost("/api/discord/v10/applications/{applicationId}/commands")]
    public Task<IResult> CreateGlobalAsync(string applicationId, DiscordCommandDto dto,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx) =>
        UpsertOneAsync(applicationId, null, dto, user, ctx);

    [WolverineDelete("/api/discord/v10/applications/{applicationId}/commands/{commandId}")]
    public Task<IResult> DeleteGlobalAsync(string applicationId, string commandId,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx) =>
        DeleteOneAsync(applicationId, commandId, user, ctx);

    [WolverinePut("/api/discord/v10/applications/{applicationId}/guilds/{guildId}/commands")]
    public Task<IResult> BulkOverwriteGuildAsync(string applicationId, string guildId, List<DiscordCommandDto> dtos,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx) =>
        BulkOverwriteAsync(applicationId, guildId, dtos, user, ctx);

    [WolverineGet("/api/discord/v10/applications/{applicationId}/guilds/{guildId}/commands")]
    public Task<IResult> ListGuildAsync(string applicationId, string guildId,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx) =>
        ListAsync(applicationId, guildId, user, ctx);

    [WolverinePost("/api/discord/v10/applications/{applicationId}/guilds/{guildId}/commands")]
    public Task<IResult> CreateGuildAsync(string applicationId, string guildId, DiscordCommandDto dto,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx) =>
        UpsertOneAsync(applicationId, guildId, dto, user, ctx);

    [WolverineDelete("/api/discord/v10/applications/{applicationId}/guilds/{guildId}/commands/{commandId}")]
    public Task<IResult> DeleteGuildAsync(string applicationId, string guildId, string commandId,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx) =>
        DeleteOneAsync(applicationId, commandId, user, ctx);

    private static async Task<BotApplication?> AuthorizeAsync(string applicationId, ClaimsPrincipal user, MicroserviceContext ctx)
    {
        var callerId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(callerId) || callerId != applicationId) return null;

        return await ctx.BotApplications.FirstOrDefaultAsync(a => a.BotUserId == applicationId && a.IsEnabled);
    }

    private static async Task<IResult> BulkOverwriteAsync(string applicationId, string? guildId, List<DiscordCommandDto> dtos,
        ClaimsPrincipal user, MicroserviceContext ctx)
    {
        var app = await AuthorizeAsync(applicationId, user, ctx);
        if (app is null) return Results.Forbid();

        var existing = await ctx.BotCommands
            .Where(c => c.BotApplicationId == app.Id && c.GuildId == guildId)
            .ToListAsync();
        ctx.BotCommands.RemoveRange(existing);

        var created = dtos.Select(dto => new BotCommand
        {
            Id = BotCommand.GenerateId(),
            BotApplicationId = app.Id,
            Name = dto.Name,
            Description = dto.Description ?? "",
            OptionsJson = dto.Options?.GetRawText() ?? "[]",
            GuildId = guildId,
        }).ToList();
        ctx.BotCommands.AddRange(created);

        return Results.Ok(created.Select(c => ToResponseShape(c, app.BotUserId)));
    }

    private static async Task<IResult> ListAsync(string applicationId, string? guildId, ClaimsPrincipal user, MicroserviceContext ctx)
    {
        var app = await AuthorizeAsync(applicationId, user, ctx);
        if (app is null) return Results.Forbid();

        var commands = await ctx.BotCommands
            .AsNoTracking()
            .Where(c => c.BotApplicationId == app.Id && c.GuildId == guildId)
            .ToListAsync();

        return Results.Ok(commands.Select(c => ToResponseShape(c, app.BotUserId)));
    }

    private static async Task<IResult> UpsertOneAsync(string applicationId, string? guildId, DiscordCommandDto dto,
        ClaimsPrincipal user, MicroserviceContext ctx)
    {
        var app = await AuthorizeAsync(applicationId, user, ctx);
        if (app is null) return Results.Forbid();

        // Matches Discord's real POST-one semantics: creating with the same name as an existing
        // command in this scope edits it in place rather than erroring or duplicating.
        var existing = await ctx.BotCommands
            .FirstOrDefaultAsync(c => c.BotApplicationId == app.Id && c.GuildId == guildId && c.Name == dto.Name);

        if (existing is not null)
        {
            existing.Description = dto.Description ?? "";
            existing.OptionsJson = dto.Options?.GetRawText() ?? "[]";
            return Results.Ok(ToResponseShape(existing, app.BotUserId));
        }

        var command = new BotCommand
        {
            Id = BotCommand.GenerateId(),
            BotApplicationId = app.Id,
            Name = dto.Name,
            Description = dto.Description ?? "",
            OptionsJson = dto.Options?.GetRawText() ?? "[]",
            GuildId = guildId,
        };
        ctx.BotCommands.Add(command);

        return Results.Ok(ToResponseShape(command, app.BotUserId));
    }

    private static async Task<IResult> DeleteOneAsync(string applicationId, string commandId, ClaimsPrincipal user, MicroserviceContext ctx)
    {
        var app = await AuthorizeAsync(applicationId, user, ctx);
        if (app is null) return Results.Forbid();

        var command = await ctx.BotCommands.FirstOrDefaultAsync(c => c.Id == commandId && c.BotApplicationId == app.Id);
        if (command is null) return Results.NotFound();

        ctx.BotCommands.Remove(command);
        return Results.NoContent();
    }

    private static object ToResponseShape(BotCommand command, string botUserId) => new
    {
        id = command.Id,
        application_id = botUserId,
        name = command.Name,
        description = command.Description,
        type = 1,
        options = JsonSerializer.Deserialize<JsonElement>(command.OptionsJson),
        guild_id = command.GuildId,
    };
}
