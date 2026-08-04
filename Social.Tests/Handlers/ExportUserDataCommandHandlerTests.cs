using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Social.Api.Commands;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

/// <summary>Social's participant in the export fan-out (T1-7).</summary>
[TestFixture]
public class ExportUserDataCommandHandlerTests
{
    private TestSocialContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestSocialContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private Task<Identity.Contracts.Bus.Response.ExportUserDataResponse> ExportAsync(string userId) =>
        ExportUserDataCommandHandler.Handle(
            new ExportUserDataCommand { ExportId = "dxrq_test", UserId = userId }, _context);

    private async Task<Profile> SeedProfileAsync(string userId, string username)
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = userId, Username = username });
        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();
        return profile;
    }

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_ExportsTheSubjectsOwnProfile()
    {
        var profile = await SeedProfileAsync("user_subject", "subject");
        profile.Bio = "my own bio";
        profile.AccentColor = "#5865F2";
        profile.Font = ProfileFont.Serif;
        await _context.SaveChangesAsync();

        var response = await ExportAsync("user_subject");

        Assert.That(response.Service, Is.EqualTo("social"));
        Assert.That(response.RowCounts["profile"], Is.EqualTo(1));

        using var document = JsonDocument.Parse(response.FragmentJson);
        var exported = document.RootElement.GetProperty("profile");

        Assert.Multiple(() =>
        {
            Assert.That(exported.GetProperty("userName").GetString(), Is.EqualTo("subject"));
            Assert.That(exported.GetProperty("bio").GetString(), Is.EqualTo("my own bio"));
            Assert.That(exported.GetProperty("accentColor").GetString(), Is.EqualTo("#5865F2"));
        });
    }

    [Test]
    public async Task Handle_ExportsTheSubjectsSideOfEachRelationship()
    {
        var subject = await SeedProfileAsync("user_subject", "subject");
        var friend = await SeedProfileAsync("user_friend", "friend");

        var pair = Relationship.Create(new CreateRelationshipParams
        {
            Initiator = subject.Id,
            Subject = friend.Id,
        });

        _context.Relationships.AddRange(pair);
        await _context.SaveChangesAsync();

        var response = await ExportAsync("user_subject");

        Assert.That(response.RowCounts["relationships"], Is.EqualTo(1));

        using var document = JsonDocument.Parse(response.FragmentJson);
        var relationship = document.RootElement.GetProperty("relationships").EnumerateArray().Single();

        Assert.That(relationship.GetProperty("counterpartyProfileId").GetString(), Is.EqualTo(friend.Id));
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_AccountWithNoProfile_ReturnsAnEmptyFragmentRatherThanThrowing()
    {
        var response = await ExportAsync("user_noprofile");

        Assert.Multiple(() =>
        {
            Assert.That(response.Service, Is.EqualTo("social"));
            Assert.That(response.RowCounts["profile"], Is.EqualTo(0));
            Assert.That(response.RowCounts["relationships"], Is.EqualTo(0));
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_DoesNotIncludeACounterpartysProfileData()
    {
        var subject = await SeedProfileAsync("user_subject", "subject");
        var friend = await SeedProfileAsync("user_friend", "SuperSecretUsername");
        friend.Bio = "the friend's own bio";
        friend.AccentColor = "#ABCDEF";
        await _context.SaveChangesAsync();

        var pair = Relationship.Create(new CreateRelationshipParams
        {
            Initiator = subject.Id,
            Subject = friend.Id,
        });

        _context.Relationships.AddRange(pair);
        await _context.SaveChangesAsync();

        var response = await ExportAsync("user_subject");

        Assert.Multiple(() =>
        {
            Assert.That(response.FragmentJson, Does.Not.Contain("SuperSecretUsername"));
            Assert.That(response.FragmentJson, Does.Not.Contain("the friend's own bio"));
            Assert.That(response.FragmentJson, Does.Not.Contain("#ABCDEF"));
            Assert.That(response.FragmentJson, Does.Not.Contain("user_friend"));
        });
    }

    [Test]
    public async Task Handle_DoesNotDiscloseBlocksPlacedAgainstTheSubject()
    {
        var subject = await SeedProfileAsync("user_subject", "subject");
        var blocker = await SeedProfileAsync("user_blocker", "blocker");

        // T0-3: a block must not be visible to the person blocked.
        var block = new Relationship
        {
            Id = "rlsp_block",
            OwnerId = blocker.Id,
            TargetId = subject.Id,
            Status = RelationshipStatus.Blocked,
        };

        _context.Relationships.Add(block);
        await _context.SaveChangesAsync();

        var response = await ExportAsync("user_subject");

        Assert.Multiple(() =>
        {
            Assert.That(response.RowCounts["relationships"], Is.EqualTo(0));
            Assert.That(response.FragmentJson, Does.Not.Contain("Blocked"));
        });
    }
}
