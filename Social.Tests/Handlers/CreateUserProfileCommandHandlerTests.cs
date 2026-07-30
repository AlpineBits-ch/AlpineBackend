using Microsoft.Extensions.Logging.Abstractions;
using Social.Api.Commands;
using Social.Contracts.Bus.Commands;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

[TestFixture]
public class CreateUserProfileCommandHandlerTests
{
    private string _dbName = null!;
    private TestSocialContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestSocialContext(_dbName);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public void Handle_CreatesProfileAndReturnsMatchingIds()
    {
        var command = new CreateUserProfileCommand
        {
            UserId = "user-1",
            Username = "tester",
            Bio = "hello",
        };

        var response = CreateUserProfileCommandHandler.Handle(
            command, _context, NullLogger<CreateUserProfileCommandHandler>.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(response.UserId, Is.EqualTo("user-1"));
            Assert.That(response.ProfileId, Is.Not.Null.And.Not.Empty);
            Assert.That(response.Errors, Is.Empty);
        });
    }

    [Test]
    public async Task Handle_AddsProfileToContext_PersistsOnSave()
    {
        var command = new CreateUserProfileCommand
        {
            UserId = "user-2",
            Username = "tester2",
            Bio = "hello",
        };

        var response = CreateUserProfileCommandHandler.Handle(
            command, _context, NullLogger<CreateUserProfileCommandHandler>.Instance);

        // Handler is dispatched via the bus in production and relies on Wolverine's
        // DbContext middleware to commit - simulate that single commit here.
        await _context.SaveChangesAsync();

        var stored = _context.Profiles.Single(p => p.Id == response.ProfileId);
        Assert.Multiple(() =>
        {
            Assert.That(stored.UserId, Is.EqualTo("user-2"));
            Assert.That(stored.UserName, Is.EqualTo("tester2"));
        });
    }
}
