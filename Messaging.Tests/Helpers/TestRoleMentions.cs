using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;

namespace Messaging.Tests.Helpers;

/// <summary>
/// The Guild-side answer to <see cref="ResolveRoleMentionsRequest"/> for fixtures that are not
/// about the role-mention gate.
/// </summary>
public static class TestRoleMentions
{
    public static ResolveRoleMentionsResponse AllMentionable(ResolveRoleMentionsRequest request) =>
        new() { MentionableRoleIds = request.RoleIds.ToList() };
}
