using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Endpoints;
using Messaging.Application.Services;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// Covers SearchEndpoint's validation, auth, and permission-check paths - everything that runs
/// before the actual full-text query.
/// </summary>
[TestFixture]
public class SearchEndpointTests
{
    private TestMessagingContext _context = null!;
    private EfCoreMessageRepository _repo = null!;
    private FakeDistributedCache _cache = null!;
    private ConversationPermissionService _permissionService = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMessageRepository(_context);
        _cache = new FakeDistributedCache();
        _permissionService = new ConversationPermissionService(_context, _cache);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task Search_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new SearchEndpoint();
        var bus = new FakeMessageBus();

        var result = await endpoint.SearchMessages("hello", "chan-1", null, 25, _context, _repo, _permissionService, TestPrincipal.Anonymous(), bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Search_EmptyQuery_ReturnsBadRequest()
    {
        var endpoint = new SearchEndpoint();
        var bus = new FakeMessageBus();

        var result = await endpoint.SearchMessages("   ", "chan-1", null, 25, _context, _repo, _permissionService, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Search_NoChannelOrConversationId_ReturnsBadRequest()
    {
        var endpoint = new SearchEndpoint();
        var bus = new FakeMessageBus();

        var result = await endpoint.SearchMessages("hello", null, null, 25, _context, _repo, _permissionService, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Search_ChannelScope_UserLacksViewChannelPermission_ReturnsForbid()
    {
        var endpoint = new SearchEndpoint();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = r.Permission },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.SearchMessages("hello", "chan-1", null, 25, _context, _repo, _permissionService, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Search_ChannelScope_PermissionCheckUsesViewChannelPermission()
    {
        var endpoint = new SearchEndpoint();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest => new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = ExternalPermission.ViewChannel },
            _ => throw new InvalidOperationException("unexpected"),
        });

        await endpoint.SearchMessages("hello", "chan-1", null, 25, _context, _repo, _permissionService, TestPrincipal.ForUser("user-1"), bus);

        var request = (HasUserPermissionToChannelRequest)bus.Invoked.Single();
        Assert.That(request.Permission, Is.EqualTo(ExternalPermission.ViewChannel));
    }

    [Test]
    public async Task Search_ConversationScope_UserLacksPermission_ReturnsForbid()
    {
        var endpoint = new SearchEndpoint();
        var bus = new FakeMessageBus();

        // No members added, so ConversationPermissionService.HasPermission returns false.
        var result = await endpoint.SearchMessages("hello", null, "conv-1", 25, _context, _repo, _permissionService, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }
}
