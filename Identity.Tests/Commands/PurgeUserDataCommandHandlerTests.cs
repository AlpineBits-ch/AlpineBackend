using Identity.Application.Commands;
using Identity.Contracts.Bus.Commands;
using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Identity.Tests.Commands;

/// <summary>Identity's own participant in the cross-service AccountDeletionSaga fan-out. This is
/// a Wolverine bus handler (dispatched via IMessageBus), so per this repo's convention it must
/// never call SaveChangesAsync itself - Wolverine's transactional middleware commits for it in
/// production. Tests here call SaveChangesAsync explicitly afterwards to simulate that middleware
/// (same approach as Guild.Tests/Bus/Consumers/ChannelSyncHandlersTests).</summary>
[TestFixture]
public class PurgeUserDataCommandHandlerTests
{
    private TestIdentityContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ApplicationUser SeedUser(TestIdentityContext ctx)
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"purge-{Guid.NewGuid():N}@example.com",
            Username = $"purge{Guid.NewGuid():N}"[..12],
            PhoneNumber = null!,
            BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-20)),
        });
        ctx.Users.Add(user);
        ctx.SaveChanges();
        return user;
    }

    [Test]
    public async Task Handle_ExistingUser_TombstonesTheAccount()
    {
        var user = SeedUser(_context);

        var response = await PurgeUserDataCommandHandler.Handle(
            new PurgeUserDataCommand { UserId = user.Id }, _context);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Users.FirstAsync(u => u.Id == user.Id);
        Assert.That(reloaded.Status, Is.EqualTo(UserStatus.Deleted));
        Assert.That(reloaded.Email, Is.Null);
        Assert.That(response.UserId, Is.EqualTo(user.Id));
        Assert.That(response.Service, Is.EqualTo("identity"));
    }

    [Test]
    public async Task Handle_UnknownUserId_ReturnsResponseWithoutThrowing()
    {
        var response = await PurgeUserDataCommandHandler.Handle(
            new PurgeUserDataCommand { UserId = "user_doesnotexist" }, _context);

        Assert.That(response.UserId, Is.EqualTo("user_doesnotexist"));
        Assert.That(response.Service, Is.EqualTo("identity"));
    }

    [Test]
    public async Task Handle_CalledTwiceForSameUser_IsIdempotent()
    {
        var user = SeedUser(_context);

        await PurgeUserDataCommandHandler.Handle(new PurgeUserDataCommand { UserId = user.Id }, _context);
        await _context.SaveChangesAsync();
        var userNameAfterFirst = (await _context.Users.FirstAsync(u => u.Id == user.Id)).UserName;

        // Simulates a redelivered command (e.g. saga retry after a partial failure).
        await PurgeUserDataCommandHandler.Handle(new PurgeUserDataCommand { UserId = user.Id }, _context);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Users.FirstAsync(u => u.Id == user.Id);
        Assert.That(reloaded.UserName, Is.EqualTo(userNameAfterFirst));
        Assert.That(reloaded.Status, Is.EqualTo(UserStatus.Deleted));
    }
}
