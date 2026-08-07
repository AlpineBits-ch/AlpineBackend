using System.Text.RegularExpressions;

namespace Echo.Voice.Tests.Architecture;

/// <summary>
/// Fails the build when a voice event is pushed from anywhere except <see
/// cref="Echo.Voice.Rooms.VoiceAnnouncer"/>.
/// </summary>
[TestFixture]
public class VoiceEventOwnershipTests
{
    /// <summary>Anything sent under these prefixes is room state a client has to be able to
    /// reconstruct.</summary>
    private static readonly Regex VoiceEventSend = new(
        """SendAsync\(\s*"(guild\.voice\.|call\.)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Pre-unification emission sites, grandfathered so the check can land before the migration
    /// finishes.
    /// </summary>
    private static readonly HashSet<string> Grandfathered = new(StringComparer.OrdinalIgnoreCase)
    {
        // Guild.
        "Guild.Application/Bus/Events/Realtime/GuildVoiceStateHandler.cs",
        "Guild.Application/Bus/Events/Realtime/GuildLifecycleHandler.cs",
        "Guild.Application/Controllers/GuildVoiceController.cs",
        "Guild.Application/Services/VoiceHeartbeatCleanupService.cs",

        // Messaging - the share-viewer broadcast, which predates the announcer.
        "Messaging.Application/Controllers/VoiceController.cs",

        // Messaging - call ring lifecycle.
        "Messaging.Application/Services/CallEndNotifier.cs",
        "Messaging.Application/Handler/Call/CallAcceptedHandler.cs",
        "Messaging.Application/Handler/Call/CallDeclinedHandler.cs",
        "Messaging.Application/Handler/Call/CallParticipantLeftHandler.cs",
        "Messaging.Application/Handler/Call/CallWentAloneHandler.cs",
        "Messaging.Application/Handler/Call/CallDeviceTakeoverHandler.cs",
        "Messaging.Application/Handler/Call/CallDeviceDismissedHandler.cs",
    };

    private static readonly string[] ScannedProjects = ["Guild.Application", "Messaging.Application"];

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Echo.sln")))
            dir = dir.Parent;

        Assert.That(dir, Is.Not.Null, "could not locate the repository root (Echo.sln)");
        return dir!;
    }

    private static List<string> OffendingFiles()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var project in ScannedProjects)
        {
            var projectDir = new DirectoryInfo(Path.Combine(root.FullName, project));
            if (!projectDir.Exists) continue;

            foreach (var file in projectDir.EnumerateFiles("*.cs", SearchOption.AllDirectories))
            {
                if (file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                if (!VoiceEventSend.IsMatch(File.ReadAllText(file.FullName))) continue;

                offenders.Add(Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/'));
            }
        }

        return offenders;
    }

    [Test]
    public void No_new_voice_event_is_sent_from_outside_the_announcer()
    {
        var unexpected = OffendingFiles().Where(f => !Grandfathered.Contains(f)).ToList();

        Assert.That(unexpected, Is.Empty,
            "These files push a voice event directly instead of going through VoiceAnnouncer, so the "
            + "payload carries no room version and a client that misses it can never find out. Route "
            + "them through VoiceRoomService/VoiceAnnouncer. Do not add them to the allowlist - it "
            + "exists only to grandfather the pre-unification code, and it must never grow.");
    }

    /// <summary>Keeps the allowlist honest.</summary>
    [Test]
    public void The_allowlist_contains_no_entries_that_have_already_been_migrated()
    {
        var offenders = OffendingFiles().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = Grandfathered.Where(f => !offenders.Contains(f)).ToList();

        Assert.That(stale, Is.Empty,
            "These files no longer send voice events directly, so their exemption is obsolete and "
            + "should be deleted from Grandfathered.");
    }

    /// <summary>Guards the guard: a typo in the pattern would turn both tests above into
    /// unconditional passes, which is worse than not having them.</summary>
    [Test]
    public void The_detection_pattern_actually_matches_a_direct_send()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VoiceEventSend.IsMatch("""hub.Clients.User(id).SendAsync("guild.voice.MuteChanged", p)"""), Is.True);
            Assert.That(VoiceEventSend.IsMatch("""hub.Clients.Users(ids).SendAsync("call.TrackPublished", p)"""), Is.True);
            Assert.That(VoiceEventSend.IsMatch("""hub.Clients.User(id).SendAsync("presence.UserOffline", id)"""), Is.False);
        });
    }
}
