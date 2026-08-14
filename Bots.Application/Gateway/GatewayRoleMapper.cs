using System.Globalization;
using Bots.Contracts.Gateway.Payloads;
using Guild.Contracts.Bus.Response;

namespace Bots.Application.Gateway;

/// <summary>Turns Guild's <see cref="RoleSnapshot"/> into Discord's role object.</summary>
public static class GatewayRoleMapper
{
    public static GatewayRolePayload ToPayload(RoleSnapshot role) => new()
    {
        Id = role.Id,
        Name = role.Name,
        Color = ParseHexColor(role.Color),
        Position = role.Position,
        // Decimal string, which is what Discord sends and what a bot library's bitfield parser
        // expects.
        Permissions = role.Permissions.ToString(CultureInfo.InvariantCulture),
        Managed = role.Managed,
        Mentionable = role.Mentionable,
        Hoist = role.Hoist,
        Icon = role.IconUrl,
        UnicodeEmoji = role.UnicodeEmoji,
        Tags = BuildTags(role),
    };

    /// <summary>Null for a role nothing owns.</summary>
    private static GatewayRoleTagsPayload? BuildTags(RoleSnapshot role)
    {
        var hasBot = !string.IsNullOrWhiteSpace(role.BotUserId);
        var hasIntegration = !string.IsNullOrWhiteSpace(role.IntegrationId);
        if (!hasBot && !hasIntegration) return null;

        return new GatewayRoleTagsPayload
        {
            BotId = hasBot ? role.BotUserId : null,
            IntegrationId = hasIntegration ? role.IntegrationId : null,
        };
    }

    /// <summary>Echo stores colours as free-form strings (R16), so anything that is not a hex
    /// triple resolves to 0 - Discord's "no colour", and the same answer a blank column gives.</summary>
    public static int ParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return 0;
        var value = hex.TrimStart('#');
        return int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }
}
