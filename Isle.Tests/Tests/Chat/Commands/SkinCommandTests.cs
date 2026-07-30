using FluentValidation.Results;
using Isle.Api.Chat;
using Isle.Api.Chat.Commands;
using Isle.Contracts.Commands;
using Isle.Tests.Helpers;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class SkinCommandTests
{
    private TestIsleContext _context = null!;
    private ISkinStore _store = null!;
    private IBridgeClient _client = null!;
    private IMessageBus _bus = null!;
    private SkinCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _store = Substitute.For<ISkinStore>();
        _client = Substitute.For<IBridgeClient>();
        _bus = Substitute.For<IMessageBus>();
        _command = new SkinCommand(_context, _store, _client, _bus);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task ExecuteAsync_NoArguments_ReturnsUsageMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = [] });

        Assert.That(result, Does.Contain("Usage: skin create|manage|delete"));
    }

    [Test]
    public async Task ExecuteAsync_UnknownVerb_ReturnsUsageMessage()
    {
        var result = await _command.ExecuteAsync(new CommandContext { Arguments = ["bogus"] });

        Assert.That(result, Does.Contain("Usage: skin create|manage|delete"));
    }

    [Test]
    public async Task Create_NoValidationErrors_ReturnsSuccessMessageAndSendsParsedCustomizer()
    {
        _bus.InvokeAsync<CreateSkinCommandResponse>(Arg.Any<CreateSkinCommand>())
            .Returns(new CreateSkinCommandResponse { SkinId = "skin_1" });

        var result = await _command.ExecuteAsync(new CommandContext
        {
            PlayerId = "player_1", Arguments = ["create", "body=FF0000"],
        });

        Assert.That(result, Is.EqualTo("Skin has been successfully created"));

        await _bus.Received(1).InvokeAsync<CreateSkinCommandResponse>(Arg.Is<CreateSkinCommand>(
            c => c.Parameter.PlayerId == "player_1"
                 && c.Parameter.Species == Species.Tyrannosaurus
                 && c.Parameter.Customizer.BodyColor!.R == 1.0));
    }

    [Test]
    public async Task Create_ValidationErrors_ReturnsJoinedErrorMessages()
    {
        _bus.InvokeAsync<CreateSkinCommandResponse>(Arg.Any<CreateSkinCommand>())
            .Returns(new CreateSkinCommandResponse
            {
                Errors =
                [
                    new ValidationFailure("Species", "Species is invalid"),
                    new ValidationFailure("Customizer", "Customizer is invalid"),
                ],
            });

        var result = await _command.ExecuteAsync(new CommandContext { PlayerId = "player_1", Arguments = ["create"] });

        Assert.That(result, Does.Contain("Species is invalid"));
        Assert.That(result, Does.Contain("Customizer is invalid"));
    }

    [Test]
    public async Task Apply_NoStoredSkin_ReturnsNoSkinMessage()
    {
        _store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((SkinCustomizer?)null);

        var result = await _command.ExecuteAsync(new CommandContext { PlayerSteam = "steam-1", Arguments = ["apply"] });

        Assert.That(result, Is.EqualTo("You don't have a skin"));
        await _client.DidNotReceive().SetSkinAsync(Arg.Any<string>(), Arg.Any<SkinCustomizer>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_StoredSkin_AppliesItAndReturnsSuccessMessage()
    {
        var skin = new SkinCustomizer();
        _store.GetAsync("steam-1", Arg.Any<CancellationToken>()).Returns(skin);

        var result = await _command.ExecuteAsync(new CommandContext { PlayerSteam = "steam-1", Arguments = ["apply"] });

        Assert.That(result, Is.EqualTo("Skin has been successfully applied"));
        await _client.Received(1).SetSkinAsync("steam-1", skin, Arg.Any<CancellationToken>());
    }

    [Test]
    public void Manage_ThrowsNotImplemented()
    {
        Assert.ThrowsAsync<NotImplementedException>(async () =>
            await _command.ExecuteAsync(new CommandContext { Arguments = ["manage"] }));
    }

    [Test]
    public void Delete_ThrowsNotImplemented()
    {
        Assert.ThrowsAsync<NotImplementedException>(async () =>
            await _command.ExecuteAsync(new CommandContext { Arguments = ["delete"] }));
    }

    [Test]
    public void Name_IsSkin()
    {
        Assert.That(_command.Name, Is.EqualTo("skin"));
    }

    [Test]
    public void IsAdminOnly_IsTrue()
    {
        Assert.That(_command.IsAdminOnly, Is.True);
    }

    [Test]
    public void CanRun_NonAdminContext_ReturnsFalse()
    {
        Assert.That(_command.CanRun(new CommandContext { IsAdmin = false, Arguments = [] }), Is.False);
    }
}
