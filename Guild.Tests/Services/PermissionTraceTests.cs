using Guild.Application.Services;
using Guild.Domain.Enums;

namespace Guild.Tests.Services;

/// <summary>The trace records who last wrote each bit, and changes no result.</summary>
[TestFixture]
public class PermissionTraceTests
{
    [Test]
    public void Record_LastWriterWins()
    {
        var trace = new PermissionTrace();

        trace.Record(Permissions.SendMessages, PermissionSource.CategoryRoleAllow);
        trace.Record(Permissions.SendMessages, PermissionSource.ChannelEveryoneDeny);

        Assert.That(trace.SourceOf(Permissions.SendMessages), Is.EqualTo(PermissionSource.ChannelEveryoneDeny));
    }

    [Test]
    public void Record_SplitsAMultiBitMaskIntoOneEntryPerBit()
    {
        var trace = new PermissionTrace();

        trace.Record(Permissions.SendMessages | Permissions.AttachFiles, PermissionSource.ChannelRoleAllow);

        Assert.Multiple(() =>
        {
            Assert.That(trace.SourceOf(Permissions.SendMessages), Is.EqualTo(PermissionSource.ChannelRoleAllow));
            Assert.That(trace.SourceOf(Permissions.AttachFiles), Is.EqualTo(PermissionSource.ChannelRoleAllow));
        });
    }

    [Test]
    public void RecordDeny_AttributesUnnamedBitsToImplication()
    {
        var trace = new PermissionTrace();

        // Denying ViewChannel takes SendMessages with it, but only ViewChannel was named.
        trace.RecordDeny(
            changed: Permissions.ViewChannel | Permissions.SendMessages,
            named: Permissions.ViewChannel,
            PermissionLayer.Channel,
            PermissionTier.Everyone);

        Assert.Multiple(() =>
        {
            Assert.That(trace.SourceOf(Permissions.ViewChannel), Is.EqualTo(PermissionSource.ChannelEveryoneDeny));
            Assert.That(trace.SourceOf(Permissions.SendMessages), Is.EqualTo(PermissionSource.Implied));
        });
    }

    [Test]
    public void SourceOf_UntouchedBitIsBase()
    {
        var trace = new PermissionTrace();

        Assert.That(trace.SourceOf(Permissions.Connect), Is.EqualTo(PermissionSource.Base));
    }

    /// <summary>The whole point of a sink rather than a second resolver: tracing changes no
    /// answer. ApplyOverwrites and OverwriteTiers are internal so this calls them directly.</summary>
    [Test]
    public void ApplyOverwrites_TracingDoesNotChangeTheResult()
    {
        var tiers = new GuildPermissionService.OverwriteTiers(
            Permissions.ViewChannel, Permissions.AddReactions,
            Permissions.MentionEveryone, Permissions.SendMessages,
            Permissions.None, Permissions.Connect,
            ModulePermissions.None, ModulePermissions.None,
            ModulePermissions.None, ModulePermissions.None,
            ModulePermissions.None, ModulePermissions.None);

        var start = Permissions.ViewChannel | Permissions.SendMessages | Permissions.PinMessages;

        var untraced = GuildPermissionService.ApplyOverwrites(start, tiers, null, PermissionLayer.Channel);
        var traced = GuildPermissionService.ApplyOverwrites(start, tiers, new PermissionTrace(), PermissionLayer.Channel);

        Assert.That(traced, Is.EqualTo(untraced));
    }
}
