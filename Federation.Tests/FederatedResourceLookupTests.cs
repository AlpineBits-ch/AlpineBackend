using Federation.Application.Bus.Outbound;
using Federation.Domain.Aggregates;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Federation.Tests;

[TestFixture]
public class FederatedResourceLookupTests
{
    private MicroserviceContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        var opts = new DbContextOptionsBuilder<MicroserviceContext>()
            .UseInMemoryDatabase($"lookup-{Guid.NewGuid()}")
            .Options;
        _db = new MicroserviceContext(opts);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private FederationInstance MakeInstance(string host, FederationStatus status = FederationStatus.Active)
    {
        var instance = new FederationInstance
        {
            Id = $"fein_{Guid.NewGuid():N}",
            Host = host,
            Name = host,
            Status = status,
        };
        _db.FederationInstances.Add(instance);
        return instance;
    }

    [Test]
    public async Task GetActiveInstances_ReturnsOnlyActiveLinkedInstances()
    {
        var active = MakeInstance("https://active.example.com");
        var blocked = MakeInstance("https://blocked.example.com", FederationStatus.Blocked);

        _db.FederatedResources.Add(new FederatedResource { Id = FederatedResource.GenerateId(), LocalId = "gld_1", RemoteId = "gld_1", ResourceType = FederatedResourceType.Guild, InstanceId = active.Id });
        _db.FederatedResources.Add(new FederatedResource { Id = FederatedResource.GenerateId(), LocalId = "gld_1", RemoteId = "gld_1", ResourceType = FederatedResourceType.Guild, InstanceId = blocked.Id });
        await _db.SaveChangesAsync();

        var result = await FederatedResourceLookup.GetActiveInstancesAsync(_db, FederatedResourceType.Guild, "gld_1", default);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Host, Is.EqualTo("https://active.example.com"));
    }

    [Test]
    public async Task GetActiveInstances_DifferentResourceType_IsExcluded()
    {
        var instance = MakeInstance("https://active.example.com");

        _db.FederatedResources.Add(new FederatedResource { Id = FederatedResource.GenerateId(), LocalId = "conv_1", RemoteId = "conv_1", ResourceType = FederatedResourceType.Conversation, InstanceId = instance.Id });
        await _db.SaveChangesAsync();

        var result = await FederatedResourceLookup.GetActiveInstancesAsync(_db, FederatedResourceType.Guild, "conv_1", default);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetActiveInstances_DifferentLocalId_IsExcluded()
    {
        var instance = MakeInstance("https://active.example.com");

        _db.FederatedResources.Add(new FederatedResource { Id = FederatedResource.GenerateId(), LocalId = "gld_other", RemoteId = "gld_other", ResourceType = FederatedResourceType.Guild, InstanceId = instance.Id });
        await _db.SaveChangesAsync();

        var result = await FederatedResourceLookup.GetActiveInstancesAsync(_db, FederatedResourceType.Guild, "gld_1", default);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetActiveInstances_NoLinkedResources_ReturnsEmpty()
    {
        var result = await FederatedResourceLookup.GetActiveInstancesAsync(_db, FederatedResourceType.Guild, "gld_unknown", default);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetActiveInstances_MultipleActiveInstances_ReturnsAll()
    {
        var a = MakeInstance("https://a.example.com");
        var b = MakeInstance("https://b.example.com");

        _db.FederatedResources.Add(new FederatedResource { Id = FederatedResource.GenerateId(), LocalId = "gld_multi", RemoteId = "gld_multi", ResourceType = FederatedResourceType.Guild, InstanceId = a.Id });
        _db.FederatedResources.Add(new FederatedResource { Id = FederatedResource.GenerateId(), LocalId = "gld_multi", RemoteId = "gld_multi", ResourceType = FederatedResourceType.Guild, InstanceId = b.Id });
        await _db.SaveChangesAsync();

        var result = await FederatedResourceLookup.GetActiveInstancesAsync(_db, FederatedResourceType.Guild, "gld_multi", default);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(i => i.Host), Is.EquivalentTo(new[] { "https://a.example.com", "https://b.example.com" }));
    }
}
