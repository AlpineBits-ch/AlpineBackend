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
    /// reconstruct. Captures the event name so the exemptions below can be judged on meaning.</summary>
    private static readonly Regex VoiceEventSend = new(
        """SendAsync\(\s*"(?:guild\.voice\.|call\.)(?<event>\w+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Events whose audience is not the room, and which therefore cannot go through <see
    /// cref="VoiceAnnouncer"/> - it addresses room participants, and these do not.
    /// </summary>
    private static readonly HashSet<string> NotRoomScoped = new(StringComparer.Ordinal)
    {
        // Guild presence and moderation fan-out.
        "UserJoinedVoice",
        "UserLeftVoice",
        "KickedByOtherDevice",
        "MovedToChannel",

        // Call ring lifecycle.
        "IncomingCall",
        "CallAccepted",
        "CallDeclined",
        "CallEnded",
        "CallAlone",
        "CallParticipantLeft",
        "CallDeviceTakeover",
        "CallDeviceDismissed",
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

    /// <summary>Every direct voice send outside the announcer, as "file: EventName".</summary>
    private static List<string> DirectSends()
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

                var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

                foreach (Match match in VoiceEventSend.Matches(File.ReadAllText(file.FullName)))
                {
                    var name = match.Groups["event"].Value;
                    if (NotRoomScoped.Contains(name)) continue;
                    offenders.Add($"{relative}: {name}");
                }
            }
        }

        return offenders;
    }

    [Test]
    public void No_room_state_event_is_sent_from_outside_the_announcer()
    {
        Assert.That(DirectSends(), Is.Empty,
            "These push a voice event directly instead of going through VoiceAnnouncer, so the "
            + "payload carries no room version or instance and a client that misses it can never "
            + "find out. Route them through VoiceRoomService/VoiceAnnouncer. Only add to "
            + "NotRoomScoped if the audience genuinely is not the room's participants.");
    }

    /// <summary>Guards the guard: a typo in the pattern would turn the check above into an
    /// unconditional pass, which is worse than not having it.</summary>
    [Test]
    public void The_detection_pattern_actually_matches_a_direct_send()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VoiceEventSend.IsMatch("""hub.Clients.User(id).SendAsync("guild.voice.MuteChanged", p)"""), Is.True);
            Assert.That(VoiceEventSend.IsMatch("""hub.Clients.Users(ids).SendAsync("call.TrackPublished", p)"""), Is.True);
            Assert.That(VoiceEventSend.IsMatch("""hub.Clients.User(id).SendAsync("presence.UserOffline", id)"""), Is.False);

            Assert.That(
                VoiceEventSend.Match("""SendAsync("guild.voice.TrackPublished", p)""").Groups["event"].Value,
                Is.EqualTo("TrackPublished"), "the event name must be captured, or every send looks exempt");
        });
    }

    /// <summary>An exemption that no longer corresponds to a real event is one nobody will notice
    /// has stopped being needed, and it would silently permit a room-scoped event of that name
    /// later.</summary>
    [Test]
    public void Every_exemption_still_corresponds_to_an_event_that_is_actually_sent()
    {
        var root = RepoRoot();
        var allSends = ScannedProjects
            .Select(p => new DirectoryInfo(Path.Combine(root.FullName, p)))
            .Where(d => d.Exists)
            .SelectMany(d => d.EnumerateFiles("*.cs", SearchOption.AllDirectories))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => VoiceEventSend.Matches(File.ReadAllText(f.FullName))
                .Select(m => m.Groups["event"].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.That(NotRoomScoped.Where(e => !allSends.Contains(e)), Is.Empty,
            "these exemptions name events nothing sends any more");
    }
}
