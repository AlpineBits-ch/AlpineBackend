using System.Text.Json;
using Bots.Application.Commands;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Identity.Contracts.Bus.Commands;

namespace Bots.Tests.Commands;

/// <summary>Bots' participant in the export fan-out (T1-7).</summary>
[TestFixture]
public class ExportUserDataCommandHandlerTests
{
    private const string Subject = "user_subject";
    private const string Other = "user_other";

    private TestBotsContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestBotsContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private Task<Identity.Contracts.Bus.Response.ExportUserDataResponse> ExportAsync(string userId) =>
        ExportUserDataCommandHandler.Handle(
            new ExportUserDataCommand { ExportId = "dxrq_test", UserId = userId }, _context);

    private async Task<BotApplication> SeedApplicationAsync(string ownerUserId, string name)
    {
        var application = new BotApplication
        {
            Id = BotApplication.GenerateId(),
            OwnerUserId = ownerUserId,
            BotUserId = $"user_bot_{Guid.NewGuid():N}"[..20],
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _context.BotApplications.Add(application);
        await _context.SaveChangesAsync();
        return application;
    }

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_ExportsTheApplicationsTheSubjectOwns()
    {
        await SeedApplicationAsync(Subject, "My Bot");

        var response = await ExportAsync(Subject);

        Assert.Multiple(() =>
        {
            Assert.That(response.Service, Is.EqualTo("bots"));
            Assert.That(response.RowCounts["applications"], Is.EqualTo(1));
        });

        using var document = JsonDocument.Parse(response.FragmentJson);
        var application = document.RootElement.GetProperty("applications").EnumerateArray().Single();

        Assert.That(application.GetProperty("name").GetString(), Is.EqualTo("My Bot"));
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_AccountWithNoApplications_ReturnsAnEmptyFragment()
    {
        var response = await ExportAsync("user_nobody");

        Assert.Multiple(() =>
        {
            Assert.That(response.RowCounts["applications"], Is.EqualTo(0));
            Assert.That(response.RowCounts["installations"], Is.EqualTo(0));
            Assert.That(response.Error, Is.Null);
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_DoesNotExportAnotherOwnersApplication()
    {
        await SeedApplicationAsync(Subject, "Mine");
        await SeedApplicationAsync(Other, "SOMEBODY ELSES BOT");

        var response = await ExportAsync(Subject);

        Assert.Multiple(() =>
        {
            Assert.That(response.RowCounts["applications"], Is.EqualTo(1));
            Assert.That(response.FragmentJson, Does.Not.Contain("SOMEBODY ELSES BOT"));
        });
    }

    [Test]
    public async Task Handle_DoesNotDiscloseWhoInstalledTheSubjectsBot()
    {
        var application = await SeedApplicationAsync(Subject, "Mine");

        _context.BotInstallations.Add(new BotInstallation
        {
            Id = BotInstallation.GenerateId(),
            BotApplicationId = application.Id,
            GuildId = "gild_somewhere",
            InstalledByUserId = Other,
            GuildMemberId = "gmbr_x",
            InstalledAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var response = await ExportAsync(Subject);

        Assert.Multiple(() =>
        {
            // The subject may see where their bot is installed; which account put it there is a
            // fact about that account.
            Assert.That(response.RowCounts["installations"], Is.EqualTo(1));
            Assert.That(response.FragmentJson, Does.Contain("gild_somewhere"));
            Assert.That(response.FragmentJson, Does.Not.Contain(Other));
        });
    }
}
