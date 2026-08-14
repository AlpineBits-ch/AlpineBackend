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

    [TestCase(35, EchoPermissions.CreateThreads)]        // CREATE_PUBLIC_THREADS
    [TestCase(36, EchoPermissions.CreatePrivateThreads)] // CREATE_PRIVATE_THREADS
    public void ToEchoPermissions_EachCreateThreadsVariant_MapsToItsOwnBit(int shift, EchoPermissions expected)
    {
        // These used to collapse into one bit, because Echo had one.
        var result = (EchoPermissions)DiscordPermissionMapper.ToEchoPermissions(Bits(shift));
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ToEchoPermissions_ManageGuildExpressions_MapsToBothExpressionBits()
    {
        // Discord retired MANAGE_EMOJIS_AND_STICKERS in favour of MANAGE_GUILD_EXPRESSIONS on the
        // same bit.
        var result = (EchoPermissions)DiscordPermissionMapper.ToEchoPermissions(Bits(30));
        Assert.That(result, Is.EqualTo(EchoPermissions.ManageExpressions | EchoPermissions.ManageEmojis));
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
        // VIEW_GUILD_INSIGHTS (19), USE_EMBEDDED_ACTIVITIES (39), USE_SOUNDBOARD (42) and
        // VIEW_CREATOR_MONETIZATION_ANALYTICS (41) have no Echo equivalent, and must stay dropped
        // rather than landing on a neighbouring bit.
        var result = (EchoPermissions)DiscordPermissionMapper.ToEchoPermissions(Bits(19, 39, 41, 42));
        Assert.That(result, Is.EqualTo(EchoPermissions.None));
    }

    [TestCase(16, EchoPermissions.ReadMessageHistory)]     // READ_MESSAGE_HISTORY
    [TestCase(18, EchoPermissions.UseExternalEmojis)]      // USE_EXTERNAL_EMOJIS
    [TestCase(37, EchoPermissions.UseExternalStickers)]    // USE_EXTERNAL_STICKERS
    [TestCase(31, EchoPermissions.UseApplicationCommands)] // USE_APPLICATION_COMMANDS
    [TestCase(8, EchoPermissions.PrioritySpeaker)]         // PRIORITY_SPEAKER
    [TestCase(32, EchoPermissions.RequestToSpeak)]         // REQUEST_TO_SPEAK
    [TestCase(25, EchoPermissions.UseVoiceActivity)]       // USE_VAD
    [TestCase(46, EchoPermissions.SendVoiceMessages)]      // SEND_VOICE_MESSAGES
    [TestCase(49, EchoPermissions.SendPolls)]              // SEND_POLLS
    [TestCase(43, EchoPermissions.CreateExpressions)]      // CREATE_EXPRESSIONS
    [TestCase(33, EchoPermissions.ManageEvents)]           // MANAGE_EVENTS
    public void ToEchoPermissions_ParityBits_MapOneToOne(int shift, EchoPermissions expected)
    {
        // Every one of these was silently dropped before the parity bits existed, so an imported
        // role arrived quietly weaker than the one the admin was looking at on Discord.
        var result = (EchoPermissions)DiscordPermissionMapper.ToEchoPermissions(Bits(shift));
        Assert.That(result, Is.EqualTo(expected));
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
