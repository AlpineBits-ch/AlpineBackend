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
 
        return new HasUserPermissionToChannelResponse
        {
            IsAllowed = isAllowed,
            UserId = request.UserId,
            ChannelId = request.ChannelId,
            Permission = request.Permission,
        };
    }
    
    
    
       private static Permissions MapToInternal(ExternalPermission permission) =>
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