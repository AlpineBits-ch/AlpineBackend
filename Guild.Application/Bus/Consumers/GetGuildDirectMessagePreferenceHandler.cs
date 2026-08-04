using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;

namespace Guild.Application.Bus.Consumers;

/// <summary>
/// Answers Messaging's <c>FriendsAndServerMembers</c> question (privacy spec T0-2 / T2-14): of the
/// guilds this recipient shares with a would-be sender, which ones has the recipient left open to
/// DMs?
///
/// <para>Guild is the only service that can answer it - it owns both the membership and the
/// override - and answering it here rather than exporting the table keeps the default rule
/// (<see cref="GuildDirectMessagePreferenceService.DefaultFor"/>) in one place.</para>
///
/// <para>Returns one response type, not a tuple: a Wolverine handler that returns a tuple also
/// <i>publishes</i> the members it did not respond with, which has already caused a duplicate
/// notification bug in this repo. There is nothing to publish here anyway - reading a preference
/// is not an event.</para>
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
