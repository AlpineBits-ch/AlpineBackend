using Federation.Application.Services;
using Federation.Domain.Aggregates;
using Federation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Federation.Tests;

[TestFixture]
public class PolicyBasedEvaluatorTests
{
    private MicroserviceContext _db = null!;
    private PolicyBasedEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        var opts = new DbContextOptionsBuilder<MicroserviceContext>()
            .UseInMemoryDatabase($"policy-{Guid.NewGuid()}")
            .Options;
        _db = new MicroserviceContext(opts);
        _evaluator = new PolicyBasedEvaluator(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task ShouldAutoAccept_NoSettingsRow_DefaultsToAutoAccept()
    {
        // No FederationSettings row exists yet - falls back to a fresh FederationSettings(),
        // whose default AcceptancePolicy is AutoAccept.
        var result = await _evaluator.ShouldAutoAcceptAsync("https://new-instance.example.com");

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task ShouldAutoAccept_SettingsRowIsAutoAccept_ReturnsTrue()
    {
        _db.FederationSettings.Add(new FederationSettings
        {
            Id = FederationSettings.SingletonId,
            AcceptancePolicy = AcceptancePolicy.AutoAccept,
        });
        await _db.SaveChangesAsync();

        var result = await _evaluator.ShouldAutoAcceptAsync("https://any.example.com");

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task ShouldAutoAccept_SettingsRowRequiresApproval_ReturnsFalse()
    {
        _db.FederationSettings.Add(new FederationSettings
        {
            Id = FederationSettings.SingletonId,
            AcceptancePolicy = AcceptancePolicy.RequireApproval,
        });
        await _db.SaveChangesAsync();

        var result = await _evaluator.ShouldAutoAcceptAsync("https://any.example.com");

        Assert.That(result, Is.False);
    }
}
