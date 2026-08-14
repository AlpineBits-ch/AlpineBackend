using Import.Application.Discord;
using Import.Application.Mapping;

namespace Import.Tests.Mapping;

[TestFixture]
public class DiscordRoleMapperTests
{
    private static DiscordRolePayload Role(string? icon = null, string? emoji = null) => new()
    {
        Id = "807654321098765432",
        Name = "VIP",
        Icon = icon,
        UnicodeEmoji = emoji,
    };

    [Test]
    public void IconUrl_HashPresent_BuildsTheCdnUrlFromRoleIdAndHash()
    {
        Assert.That(DiscordRoleMapper.IconUrl(Role(icon: "a1b2c3")),
            Is.EqualTo("https://cdn.discordapp.com/role-icons/807654321098765432/a1b2c3.png"));
    }

    [Test]
    public void IconUrl_NoHash_IsNull()
    {
        Assert.That(DiscordRoleMapper.IconUrl(Role()), Is.Null);
    }

    [Test]
    public void IconUrl_EmptyHash_IsNullRatherThanAUrlEndingInAnEmptySegment()
    {
        Assert.That(DiscordRoleMapper.IconUrl(Role(icon: "  ")), Is.Null);
    }

    [Test]
    public void UnicodeEmoji_NoIcon_IsCarriedThrough()
    {
        Assert.That(DiscordRoleMapper.UnicodeEmoji(Role(emoji: "⭐")), Is.EqualTo("⭐"));
    }

    [Test]
    public void UnicodeEmoji_BothSet_YieldsNullSoTheBadgeStaysMutuallyExclusive()
    {
        // Role.SetBadge throws when handed both, which would abort the whole import; the icon wins.
        Assert.That(DiscordRoleMapper.UnicodeEmoji(Role(icon: "a1b2c3", emoji: "⭐")), Is.Null);
    }
}
