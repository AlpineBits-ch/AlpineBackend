using System.Security.Claims;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints.Channel;

/// <summary>One resolved permission, and the layer that decided it.</summary>
public class PermissionSourceEntryDto
{
    public string Permission { get; set; } = null!;
    public bool Granted { get; set; }

    /// <summary>A <see cref="PermissionSource"/> name.</summary>
    public string DecidedBy { get; set; } = null!;
}

/// <summary>What a role or member ends up with in one channel.</summary>
public class EffectivePermissionsDto
{
    public string ChannelId { get; set; } = null!;
    public string SubjectKind { get; set; } = null!;
    public string SubjectId { get; set; } = null!;
    public Permissions Permissions { get; set; }
    public ModulePermissions ModulePermissions { get; set; }
    public List<PermissionSourceEntryDto> Sources { get; set; } = new();
}

/// <summary>The readout a permission editor needs: not just the resolved mask, but which of the
/// four layers wrote each bit. Uncached on purpose, since admin tooling is read at human speed.</summary>
[Authorize]
public class EffectivePermissionsEndpoint
{
    /// <summary>Every permission a channel overwrite can express, in the order the client groups them.</summary>
    internal static readonly Permissions[] ChannelScoped =
    [
        Permissions.ViewChannel, Permissions.CreateInvite, Permissions.UseApplicationCommands,
        Permissions.SendMessages, Permissions.ReadMessageHistory, Permissions.EditOwnMessages,
        Permissions.EditAnyMessage, Permissions.DeleteOwnMessages, Permissions.DeleteAnyMessage,
        Permissions.PinMessages, Permissions.MentionEveryone,
        Permissions.AttachFiles, Permissions.EmbedLinks, Permissions.AddReactions, Permissions.UseExternalEmojis,
        Permissions.Connect, Permissions.Speak, Permissions.Stream,
        Permissions.MuteMembers, Permissions.DeafenMembers, Permissions.MoveMembers,
        Permissions.CreateThreads, Permissions.SendMessagesInThreads,
        Permissions.ManageOwnThreads, Permissions.ManageAnyThread,
        Permissions.ManageChannel, Permissions.ManagePermissions, Permissions.ManageWebhooks,
    ];

    [WolverineGet("/api/v1/channels/{channelId}/effective-permissions")]
    public static async Task<IResult> GetEffectivePermissions(
        string channelId,
        string? roleId,
        string? memberId,
        [NotBody] GuildPermissionService permissionService,
        [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var hasRole = !string.IsNullOrWhiteSpace(roleId);
        var hasMember = !string.IsNullOrWhiteSpace(memberId);
        if (hasRole == hasMember)
            return Results.BadRequest("Pass exactly one of roleId or memberId.");

        var guildId = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => c.GuildId)
            .FirstOrDefaultAsync();

        if (guildId is null) return Results.NotFound();

        // Same audience that may write an overwrite. No MFA gate: this is a read.
        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManagePermissions))
            return Results.Forbid();

        var subject = hasRole
            ? new PermissionSubject(PermissionSubjectKind.Role, roleId!)
            : new PermissionSubject(PermissionSubjectKind.Member, memberId!);

        var resolved = await permissionService.TraceChannelPermissionsAsync(channelId, subject);
        if (resolved is null) return Results.NotFound();

        var sources = ChannelScoped
            .Select(permission => new PermissionSourceEntryDto
            {
                Permission = permission.ToString(),
                Granted = (resolved.Permissions & permission) == permission,
                DecidedBy = (resolved.Sources.TryGetValue(permission, out var source)
                    ? source
                    : PermissionSource.Base).ToString(),
            })
            .ToList();

        return Results.Ok(new EffectivePermissionsDto
        {
            ChannelId = channelId,
            SubjectKind = subject.Kind.ToString(),
            SubjectId = subject.Id,
            Permissions = resolved.Permissions,
            ModulePermissions = resolved.ModulePermissions,
            Sources = sources,
        });
    }
}
