using Guild.Application.Services;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Enums;

namespace Guild.Application.Bus.Consumers;

public  class HasUserPermissionsHandler
{
    public static async Task<HasUserPermissionToChannelResponse> Handle(
        HasUserPermissionToChannelRequest request,
        GuildPermissionService guildPermissionService)
    {
        var internalPermission = MapToInternal(request.Permission);
 
        var isAllowed = await guildPermissionService.CanUserPerformActionAsync(
            userId: request.UserId,
            channelId: request.ChannelId,
            requiredPermission: internalPermission);

        // Only resolved for an allowed SendMessages check - that is the one call site that acts on
        // them, and every other permission query would pay for values it discards.
        var slowModeSeconds = 0;
        var canBypassSlowMode = false;

        if (isAllowed && request.Permission == ExternalPermission.SendMessages)
        {
            slowModeSeconds = await guildPermissionService.GetChannelSlowModeSecondsAsync(request.ChannelId);

            // Skipped entirely when the channel has no slowmode, which is the overwhelming
            // majority of sends - there is nothing to be exempt from.
            if (slowModeSeconds > 0)
            {
                canBypassSlowMode =
                    await guildPermissionService.CanUserPerformActionAsync(request.UserId, request.ChannelId, Permissions.ManageChannel) ||
                    await guildPermissionService.CanUserPerformActionAsync(request.UserId, request.ChannelId, Permissions.ManageAnyThread);
            }
        }

        return new HasUserPermissionToChannelResponse
        {
            IsAllowed = isAllowed,
            UserId = request.UserId,
            ChannelId = request.ChannelId,
            Permission = request.Permission,
            SlowModeSeconds = slowModeSeconds,
            CanBypassSlowMode = canBypassSlowMode,
        };
    }
    
    
    
       /// <summary>
    /// Batched sibling of the handler above, used by fan-out paths (Gateway dispatch, realtime
    /// audience resolution) so that filtering N recipients costs one bus round-trip instead of N.
    /// </summary>
    public static async Task<FilterUsersWithChannelPermissionResponse> Handle(
        FilterUsersWithChannelPermissionRequest request,
        GuildPermissionService guildPermissionService)
    {
        var allowed = await guildPermissionService.FilterUsersWithChannelPermissionAsync(
            request.ChannelId,
            request.UserIds,
            MapToInternal(request.Permission));

        return new FilterUsersWithChannelPermissionResponse
        {
            ChannelId = request.ChannelId,
            AllowedUserIds = allowed,
        };
    }

    internal static Permissions MapToInternal(ExternalPermission permission) =>
        permission switch
        {
            ExternalPermission.ViewChannel        => Permissions.ViewChannel,
            ExternalPermission.SendMessages        => Permissions.SendMessages,
            ExternalPermission.EditOwnMessages     => Permissions.EditOwnMessages,
            ExternalPermission.EditAnyMessage      => Permissions.EditAnyMessage,
            ExternalPermission.DeleteOwnMessages   => Permissions.DeleteOwnMessages,
            ExternalPermission.DeleteAnyMessage    => Permissions.DeleteAnyMessage,
            ExternalPermission.PinMessages         => Permissions.PinMessages,
            ExternalPermission.AttachFiles         => Permissions.AttachFiles,
            ExternalPermission.EmbedLinks          => Permissions.EmbedLinks,
            ExternalPermission.AddReactions        => Permissions.AddReactions,
            ExternalPermission.Connect             => Permissions.Connect,
            ExternalPermission.Speak               => Permissions.Speak,
            ExternalPermission.Stream              => Permissions.Stream,
            ExternalPermission.MuteMembers         => Permissions.MuteMembers,
            ExternalPermission.DeafenMembers       => Permissions.DeafenMembers,
            ExternalPermission.MoveMembers         => Permissions.MoveMembers,
            ExternalPermission.CreateThreads       => Permissions.CreateThreads,
            ExternalPermission.SendMessagesInThreads => Permissions.SendMessagesInThreads,
            ExternalPermission.ManageOwnThreads    => Permissions.ManageOwnThreads,
            ExternalPermission.ManageAnyThread     => Permissions.ManageAnyThread,
            ExternalPermission.ManageChannel       => Permissions.ManageChannel,
            ExternalPermission.ManagePermissions   => Permissions.ManagePermissions,
            ExternalPermission.Superadmin          => Permissions.Superadmin,
            ExternalPermission.CreateInvite          => Permissions.CreateInvite,
            ExternalPermission.KickMembers            => Permissions.KickMembers,
            ExternalPermission.BanMembers             => Permissions.BanMembers,
            ExternalPermission.ModerateMembers        => Permissions.ModerateMembers,
            ExternalPermission.ManageGuild            => Permissions.ManageGuild,
            ExternalPermission.ViewAuditLog           => Permissions.ViewAuditLog,
            ExternalPermission.ManageEmojis           => Permissions.ManageEmojis,
            ExternalPermission.ManageEvents           => Permissions.ManageEvents,

            ExternalPermission.ManageLists            => Permissions.ManageLists,
            ExternalPermission.AddListItems           => Permissions.AddListItems,
            ExternalPermission.CheckOffListItems      => Permissions.CheckOffListItems,
            ExternalPermission.ManageChores           => Permissions.ManageChores,
            ExternalPermission.CompleteChores         => Permissions.CompleteChores,
            ExternalPermission.ManageLedger           => Permissions.ManageLedger,
            ExternalPermission.AddExpenses            => Permissions.AddExpenses,
            ExternalPermission.ManagePantry           => Permissions.ManagePantry,
            ExternalPermission.CreateDecisions        => Permissions.CreateDecisions,
            ExternalPermission.VoteDecisions          => Permissions.VoteDecisions,
            ExternalPermission.ManageGuests           => Permissions.ManageGuests,

            ExternalPermission.MentionEveryone        => Permissions.MentionEveryone,
            ExternalPermission.ManageRoles            => Permissions.ManageRoles,
            ExternalPermission.ManageWebhooks         => Permissions.ManageWebhooks,
            ExternalPermission.ChangeNickname         => Permissions.ChangeNickname,
            ExternalPermission.ManageNicknames        => Permissions.ManageNicknames,

            ExternalPermission.PlanMeals              => Permissions.PlanMeals,
            ExternalPermission.ManageMeals            => Permissions.ManageMeals,
            ExternalPermission.LogMaintenance         => Permissions.LogMaintenance,
            ExternalPermission.ManageMaintenance      => Permissions.ManageMaintenance,


            // Throws on unknown values rather than silently defaulting to None,
            // which would incorrectly deny every permission check for new enum
            // members added to the contract but not yet mapped here.
            _ => throw new ArgumentOutOfRangeException(
                nameof(permission),
                permission,
                $"No internal mapping defined for ExternalPermission '{permission}'. " +
                $"Add the corresponding Permissions flag and update this map.")
        };
}