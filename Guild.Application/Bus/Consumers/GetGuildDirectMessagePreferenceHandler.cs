using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;

namespace Guild.Application.Bus.Consumers;

/// <summary>
/// Answers Messaging's <c>FriendsAndServerMembers</c> question (privacy spec T0-2 / T2-14): of the
/// guilds this recipient shares with a would-be sender, which ones has the recipient left open to
/// DMs?
/// </summary>
public class GetGuildDirectMessagePreferenceHandler
{
    public static async Task<GetGuildDirectMessagePreferenceResponse> Handle(
        GetGuildDirectMessagePreferenceRequest request,
        GuildDirectMessagePreferenceService preferences)
    {
        if (string.IsNullOrWhiteSpace(request.UserId)) return new GetGuildDirectMessagePreferenceResponse();

        var resolved = await preferences.ResolveAsync(request.UserId, request.GuildIds.ToList());

        return new GetGuildDirectMessagePreferenceResponse
        {
            Preferences = resolved
                .Select(pair => new GuildDirectMessagePreferenceSummary
                {
                    GuildId = pair.Key,
                    AllowDirectMessages = pair.Value,
                })
                .ToList(),
        };
    }
}
