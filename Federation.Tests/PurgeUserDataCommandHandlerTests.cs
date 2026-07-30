using Federation.Application.Commands;
using Identity.Contracts.Bus.Commands;

namespace Federation.Tests;

/// <summary>
/// Federation's leg of the AccountDeletionSaga fan-out (see PurgeUserDataCommandHandler's own
/// remarks) is intentionally a no-op today - these tests just lock in the contract the saga
/// depends on: every participant echoes back UserId plus its own fixed Service identifier.
/// </summary>
[TestFixture]
public class PurgeUserDataCommandHandlerTests
{
    [Test]
    public async Task Handle_EchoesUserIdBackInResponse()
    {
        var command = new PurgeUserDataCommand { UserId = "usr_test123" };

        var response = await PurgeUserDataCommandHandler.Handle(command);

        Assert.That(response.UserId, Is.EqualTo("usr_test123"));
    }

    [Test]
    public async Task Handle_ReportsFederationAsTheService()
    {
        var command = new PurgeUserDataCommand { UserId = "usr_test123" };

        var response = await PurgeUserDataCommandHandler.Handle(command);

        Assert.That(response.Service, Is.EqualTo("federation"));
    }

    [Test]
    public async Task Handle_IsIdempotent_CalledTwiceProducesSameResult()
    {
        var command = new PurgeUserDataCommand { UserId = "usr_repeat" };

        var first = await PurgeUserDataCommandHandler.Handle(command);
        var second = await PurgeUserDataCommandHandler.Handle(command);

        Assert.That(first.UserId, Is.EqualTo(second.UserId));
        Assert.That(first.Service, Is.EqualTo(second.Service));
    }
}
