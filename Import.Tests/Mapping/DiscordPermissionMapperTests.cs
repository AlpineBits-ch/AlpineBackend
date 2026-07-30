using Import.Application.Mapping;

namespace Import.Tests.Mapping;

[TestFixture]
public class DiscordPermissionMapperTests
{
    private static string Bits(params int[] shifts)
    {
        var value = 0ul;
        foreach (var shift in shifts) value |= 1ul << shift;
        return value.ToString();
    }

    [TestCase(10, EchoPermissions.ViewChannel)] // VIEW_CHANNEL
    [TestCase(11, EchoPermissions.SendMessages)] // SEND_MESSAGES
    [TestCase(15, EchoPermissions.AttachFiles)] // ATTACH_FILES
    [TestCase(14, EchoPermissions.EmbedLinks)] // EMBED_LINKS
    [TestCase(6, EchoPermissions.AddReactions)] // ADD_REACTIONS
    [TestCase(20, EchoPermissions.Connect)] // CONNECT
    [TestCase(21, EchoPermissions.Speak)] // SPEAK
    [TestCase(9, EchoPermissions.Stream)] // STREAM
    [TestCase(22, EchoPermissions.MuteMembers)] // MUTE_MEMBERS
    [TestCase(23, EchoPermissions.DeafenMembers)] // DEAFEN_MEMBERS
    [TestCase(24, EchoPermissions.MoveMembers)] // MOVE_MEMBERS
    [TestCase(34, EchoPermissions.ManageAnyThread)] // MANAGE_THREADS
    [TestCase(38, EchoPermissions.SendMessagesInThreads)] // SEND_MESSAGES_IN_THREADS
    [TestCase(4, EchoPermissions.ManageChannel)] // MANAGE_CHANNELS
    [TestCase(17, EchoPermissions.MentionEveryone)] // MENTION_EVERYONE
    [TestCase(26, EchoPermissions.ChangeNickname)] // CHANGE_NICKNAME
    [TestCase(27, EchoPermissions.ManageNicknames)] // MANAGE_NICKNAMES
    [TestCase(29, EchoPermissions.ManageWebhooks)] // MANAGE_WEBHOOKS
    [TestCase(0, EchoPermissions.CreateInvite)] // CREATE_INSTANT_INVITE
    [TestCase(1, EchoPermissions.KickMembers)] // KICK_MEMBERS
    [TestCase(2, EchoPermissions.BanMembers)] // BAN_MEMBERS
    [TestCase(40, EchoPermissions.ModerateMembers)] // MODERATE_MEMBERS
    [TestCase(5, EchoPermissions.ManageGuild)] // MANAGE_GUILD
    [TestCase(7, EchoPermissions.ViewAuditLog)] // VIEW_AUDIT_LOG
    public void ToEchoPermissions_MapsSingleKnownBit(int discordBitShift, EchoPermissions expected)
    {
        var result = (EchoPermissions)DiscordPermissionMapper.ToEchoPermissions(Bits(discordBitShift));
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ToEchoPermissions_Administrator_MapsToSuperadmin()
    {
        var result = (EchoPermissions)DiscordPermissionMapper.ToEchoPermissions(Bits(3)); // ADMINISTRATOR
        Assert.That(result, Is.EqualTo(EchoPermissions.Superadmin));
    }

    [Test]
    public void ToEchoPermissions_ManageMessages_MapsToDeleteAnyAndPin()
    {
        var result = (EchoPermissions)DiscordPermissionMapper.ToEchoPermissions(Bits(13)); // MANAGE_MESSAGES
        Assert.That(result, Is.EqualTo(EchoPermissions.DeleteAnyMessage | EchoPermissions.PinMessages));
    }

    [TestCase(35)] // CREATE_PUBLIC_THREADS
    [TestCase(36)] // CREATE_PRIVATE_THREADS
    public void ToEchoPermissions_EitherCreateThreadsVariant_MapsToCreateThreads(int shift)
    {
        var result = (EchoPermissions)DiscordPermissionMapper.ToEchoPermissions(Bits(shift));
        Assert.That(result, Is.EqualTo(EchoPermissions.CreateThreads));
    }

    [Test]
    public void ToEchoPermissions_ManageRoles_MapsToBothManageRolesAndManagePermissions()
    {
        // Discord's single MANAGE_ROLES covers editing roles *and* setting per-channel overwrites;
        // Echo splits those into two bits, so one Discord bit has to light up both.
        var result = (EchoPermissions)DiscordPermissionMapper.ToEchoPermissions(Bits(28));
        Assert.That(result, Is.EqualTo(EchoPermissions.ManageRoles | EchoPermissions.ManagePermissions));
    }

    [Test]
    public void ToEchoPermissions_UnmappedDiscordBits_ProduceNoEchoPermissions()
    {
        // MANAGE_GUILD_EXPRESSIONS (30), PRIORITY_SPEAKER (8), USE_VAD (25),
        // REQUEST_TO_SPEAK (32) - none have an Echo equivalent.
        var result = (EchoPermissions)DiscordPermissionMapper.ToEchoPermissions(Bits(30, 8, 25, 32));
        Assert.That(result, Is.EqualTo(EchoPermissions.None));
    }

    [Test]
    public void ToEchoPermissions_MultipleBits_CombineCorrectly()
    {
        var result = (EchoPermissions)DiscordPermissionMapper.ToEchoPermissions(Bits(10, 11, 6)); // VIEW_CHANNEL | SEND_MESSAGES | ADD_REACTIONS
        Assert.That(result, Is.EqualTo(EchoPermissions.ViewChannel | EchoPermissions.SendMessages | EchoPermissions.AddReactions));
    }

    [Test]
    public void ToEchoPermissions_UnparseableString_ReturnsZero()
    {
        Assert.That(DiscordPermissionMapper.ToEchoPermissions("not-a-number"), Is.EqualTo(0ul));
    }
}
