using Social.Domain.Enums;

namespace Social.Api.Dtos.Response;

/// <summary>One row of the mutual-friends list, richer than the <see cref="MutualFriendDto"/> the
/// profile payload embeds because this one is rendered as a list rather than counted.</summary>
public class MutualFriendRowDto
{
    public required string ProfileId { get; init; }
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public required string AvatarUrl { get; init; }

    /// <summary>Already projected, so Hidden reads as Offline.</summary>
    public required OnlineStatus OnlineStatus { get; init; }
}

/// <summary>A page of <see cref="MutualFriendRowDto"/>, keyset-paged like the block listing.</summary>
public class MutualFriendsPageDto
{
    public IReadOnlyList<MutualFriendRowDto> Items { get; init; } = [];

    /// <summary>Opaque; pass back as <c>?cursor=</c>. Null on the last page.</summary>
    public string? NextCursor { get; init; }
}

/// <summary>One row of the mutual-servers list.</summary>
public class MutualServerRowDto
{
    public required string GuildId { get; init; }

    /// <summary>Null when the Guild build answering the bus predates named shared guilds.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// A page of <see cref="MutualServerRowDto"/>. Guild answers the whole intersection in one bus call
/// with no key to page over, so <c>NextCursor</c> is always null and the list is capped instead.
/// </summary>
public class MutualServersPageDto
{
    public IReadOnlyList<MutualServerRowDto> Items { get; init; } = [];

    public string? NextCursor { get; init; }
}
