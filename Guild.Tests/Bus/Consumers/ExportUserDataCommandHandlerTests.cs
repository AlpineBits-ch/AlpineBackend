using System.Text.Json;
using Guild.Application.Bus.Consumers;
using Guild.Domain.Entity;
using Guild.Tests.Helpers;
using Identity.Contracts.Bus.Commands;

namespace Guild.Tests.Bus.Consumers;

/// <summary>
/// Guild's participant in the export fan-out (T1-7).
///
/// <para>The subject's membership of a guild is theirs; the guild's other members are not. The
/// negative test asserts exactly that - another member's nickname must not ride along in the
/// subject's archive just because the two share a server.</para>
/// </summary>
[TestFixture]
public class ExportUserDataCommandHandlerTests
{
    private const string Subject = "user_subject";
    private const string Other = "user_other";

    private TestGuildContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestGuildContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private Task<Identity.Contracts.Bus.Response.ExportUserDataResponse> ExportAsync(string userId) =>
        ExportUserDataCommandHandler.Handle(
            new ExportUserDataCommand { ExportId = "dxrq_test", UserId = userId }, _context);

    /// <summary>Constructed directly rather than through <c>Guild.Create</c>, which also mints the
    /// owner's membership, a default category tree and an @everyone role - none of which this
    /// handler reads, and all of which would make "one membership" ambiguous.</summary>
    private async Task<global::Guild.Domain.Aggregates.Guild> SeedGuildAsync(string name)
    {
        var guild = new global::Guild.Domain.Aggregates.Guild
        {
            Id = global::Guild.Domain.Aggregates.Guild.GenerateId(),
            Name = name,
            OwnerId = Subject,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _context.Guilds.Add(guild);
        await _context.SaveChangesAsync();
        return guild;
    }

    private async Task<GuildMember> SeedMemberAsync(string guildId, string userId, string username, string? nickname)
    {
        var member = GuildMember.CreateForUser(new CreateGuildMemberParams
        {
            GuildId = guildId,
            UserId = userId,
            Username = username,
            Nickname = nickname,
        });

        _context.GuildMembers.Add(member);
        await _context.SaveChangesAsync();
        return member;
    }

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_ExportsTheSubjectsMembershipsWithTheGuildName()
    {
        var guild = await SeedGuildAsync("Test Server");
        await SeedMemberAsync(guild.Id, Subject, "subject", "My Nickname");

        var response = await ExportAsync(Subject);

        Assert.Multiple(() =>
        {
            Assert.That(response.Service, Is.EqualTo("guild"));
            Assert.That(response.RowCounts["memberships"], Is.EqualTo(1));
        });

        using var document = JsonDocument.Parse(response.FragmentJson);
        var membership = document.RootElement.GetProperty("memberships").EnumerateArray().Single();

        Assert.Multiple(() =>
        {
            // An opaque guild id is not an intelligible answer to "which servers am I in", and a
            // server's name is not personal data about anybody.
            Assert.That(membership.GetProperty("guildName").GetString(), Is.EqualTo("Test Server"));
            Assert.That(membership.GetProperty("nickname").GetString(), Is.EqualTo("My Nickname"));
        });
    }

    [Test]
    public async Task Handle_ExportsBansPlacedAgainstTheSubject()
    {
        var guild = await SeedGuildAsync("Ban Server");

        _context.GuildBans.Add(GuildBan.Create(new CreateGuildBanParams
        {
            GuildId = guild.Id,
            BannedUserId = Subject,
            BannedByUserId = Other,
            Reason = "spam",
        }));
        await _context.SaveChangesAsync();

        var response = await ExportAsync(Subject);

        using var document = JsonDocument.Parse(response.FragmentJson);
        var ban = document.RootElement.GetProperty("bans").EnumerateArray().Single();

        // A decision recorded about the subject, including its stated reason. A moderation record a
        // subject cannot obtain is exactly what Art. 15 exists for.
        Assert.That(ban.GetProperty("reason").GetString(), Is.EqualTo("spam"));
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_AccountInNoGuilds_ReturnsAnEmptyFragment()
    {
        var response = await ExportAsync("user_nobody");

        Assert.Multiple(() =>
        {
            Assert.That(response.RowCounts["memberships"], Is.EqualTo(0));
            Assert.That(response.RowCounts["bans"], Is.EqualTo(0));
            Assert.That(response.Error, Is.Null);
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_DoesNotExportOtherMembersOfTheSameGuild()
    {
        var guild = await SeedGuildAsync("Shared Server");
        await SeedMemberAsync(guild.Id, Subject, "subject", null);
        await SeedMemberAsync(guild.Id, Other, "othername", "OTHER NICKNAME");

        var response = await ExportAsync(Subject);

        Assert.Multiple(() =>
        {
            Assert.That(response.RowCounts["memberships"], Is.EqualTo(1));
            Assert.That(response.FragmentJson, Does.Not.Contain("OTHER NICKNAME"));
            Assert.That(response.FragmentJson, Does.Not.Contain("othername"));
            Assert.That(response.FragmentJson, Does.Not.Contain(Other));
        });
    }
}
