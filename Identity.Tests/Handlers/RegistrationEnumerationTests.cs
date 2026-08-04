using FluentValidation.Results;
using Identity.Application.Handlers;
using Identity.Application.Services;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Commands;
using Identity.Domain.Aggregates;
using Identity.Tests.Helpers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Identity.Tests.Handlers;

/// <summary>
/// The registration handler's branch logic, asserted where the HTTP tests cannot see it: that the
/// already-registered branch creates nothing, spends the same password-hashing time as the create
/// branch, and mails the account holder - and that the refusals which survive are decided before
/// the address is ever looked up.
/// </summary>
[TestFixture]
public class RegistrationEnumerationTests
{
    private const string Password = "SecurePass123!";

    private TestIdentityContext _context = null!;
    private IDistributedCache _cache = null!;
    private FakePasswordVerifier _passwords = null!;
    private RecordingEmailDispatcher _mail = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestIdentityContext(Guid.NewGuid().ToString());
        _cache = new ServiceCollection().AddDistributedMemoryCache()
            .BuildServiceProvider().GetRequiredService<IDistributedCache>();
        _passwords = new FakePasswordVerifier(Password);
        _mail = new RecordingEmailDispatcher();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>Records what would have been mailed instead of mailing it.</summary>
    private sealed class RecordingEmailDispatcher()
        : AccountEmailDispatcher(null!, NullLogger<AccountEmailDispatcher>.Instance)
    {
        public List<(string Email, string DisplayName, bool AwaitsVerification)> Notices { get; } = [];

        public override Task QueueRegistrationAttemptNoticeAsync(
            IDistributedCache cache, string email, string displayName, bool accountAwaitsVerification)
        {
            Notices.Add((email, displayName, accountAwaitsVerification));
            return Task.CompletedTask;
        }
    }

    private ApplicationUser SeedUser(string email, string username, bool confirmed = true)
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = email,
            Username = username,
            BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-25)),
        });
        user.EmailConfirmed = confirmed;
        user.SetPasswordHash("existing-hash");

        _context.Users.Add(user);
        _context.SaveChanges();

        return user;
    }

    private static CreateUserWithEmailAndPasswordRequest Request(string email, string username) => new()
    {
        Email = email,
        Username = username,
        Password = Password,
        BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-20)),
        IpAddress = "203.0.113.42",
    };

    private async Task<(ICollection<ValidationFailure> Failures, FakeIdentityMessageBus Bus)> HandleAsync(
        CreateUserWithEmailAndPasswordRequest request, CreateUserResponse? commandResponse = null)
    {
        var bus = new FakeIdentityMessageBus(_ => commandResponse ?? new CreateUserResponse
        {
            UserId = Guid.NewGuid().ToString(),
        });

        var response = await new CreateUserWithUsernameAndPasswordHandler().Handle(
            request,
            NullLogger<CreateUserWithUsernameAndPasswordHandler>.Instance,
            bus,
            _context,
            _passwords,
            _cache,
            _mail);

        return (response.Failures, bus);
    }

    // ── The address-exists branch ───────────────────────────────────────────

    [Test]
    public async Task ExistingAddress_IsAcceptedWithoutCreatingAnything()
    {
        SeedUser("taken@example.com", "existing_user");

        var (failures, bus) = await HandleAsync(Request("taken@example.com", "someoneelse"));

        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Empty,
                "the taken address must produce the same empty-failure response a new account does");
            Assert.That(bus.Invoked, Is.Empty,
                "no CreateUserCommand: nothing is created, so no user row, no password hash and no "
                + "T1-10 consent record stamped with the caller's IP");
        });
    }

    [Test]
    public async Task ExistingAddress_SpendsThePasswordHashingTimeTheCreateBranchSpends()
    {
        SeedUser("taken@example.com", "existing_user");

        await HandleAsync(Request("taken@example.com", "someoneelse"));

        Assert.That(_passwords.DummyCalls, Is.EqualTo(1),
            "returning without hashing makes the taken-address reply arrive tens of milliseconds "
            + "sooner than a real signup, which is the same oracle read off the clock");
    }

    [Test]
    public async Task ExistingAddress_TellsTheAccountHolderAndNobodyElse()
    {
        SeedUser("taken@example.com", "existing_user");

        await HandleAsync(Request("taken@example.com", "someoneelse"));

        Assert.That(_mail.Notices, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(_mail.Notices[0].Email, Is.EqualTo("taken@example.com"));
            Assert.That(_mail.Notices[0].DisplayName, Is.EqualTo("existing_user"),
                "the mail names the account, never the username the anonymous caller tried");
            Assert.That(_mail.Notices[0].AwaitsVerification, Is.False);
        });
    }

    [Test]
    public async Task ExistingUnverifiedAddress_GetsTheVerificationCodeItIsMissing()
    {
        // The common honest case: signed up, never found the code, signed up again.
        SeedUser("halfway@example.com", "halfway_user", confirmed: false);

        await HandleAsync(Request("halfway@example.com", "someoneelse"));

        Assert.That(_mail.Notices.Single().AwaitsVerification, Is.True);
    }

    [Test]
    public async Task LosingTheRaceToAConcurrentSignup_LandsOnTheSameAcceptedAnswer()
    {
        // The command handler's own exists check fired, which means another request created the
        // account between this one's lookup and its insert.
        SeedUser("raced@example.com", "raced_user");

        var (failures, _) = await HandleAsync(
            Request("raced@example.com", "someoneelse"),
            new CreateUserResponse { EmailAlreadyExists = true });

        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Empty);
            Assert.That(_mail.Notices, Has.Count.EqualTo(1));
        });
    }

    // ── The create branch still creates ─────────────────────────────────────

    [Test]
    public async Task UnknownAddress_IsCreatedAndNotMailedANotice()
    {
        var (failures, bus) = await HandleAsync(Request("brandnew@example.com", "brandnew"));

        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Empty);
            Assert.That(bus.Invoked.OfType<CreateUserCommand>().Count(), Is.EqualTo(1),
                "the uniform response must not have quietly become 'never create anything'");
            Assert.That(_mail.Notices, Is.Empty);
            Assert.That(_passwords.DummyCalls, Is.Zero,
                "this branch hashes the real password - the dummy is only for the branch that does not");
        });
    }

    [Test]
    public async Task UnknownAddress_CarriesTheCallersAddressOntoTheCommandForTheConsentRecord()
    {
        var (_, bus) = await HandleAsync(Request("brandnew@example.com", "brandnew"));

        Assert.That(bus.Invoked.OfType<CreateUserCommand>().Single().IpAddress, Is.EqualTo("203.0.113.42"),
            "T1-10: a consent record without an origin is materially weaker evidence");
    }

    // ── Refusals that survive, and the ordering that keeps them safe ────────

    [Test]
    public async Task TakenUsername_IsRefusedBeforeTheAddressIsEvenLookedUp()
    {
        SeedUser("taken@example.com", "existing_user");

        var (failures, bus) = await HandleAsync(Request("taken@example.com", "existing_user"));

        Assert.Multiple(() =>
        {
            Assert.That(failures.Select(f => f.PropertyName), Does.Contain("Username"));
            Assert.That(bus.Invoked, Is.Empty);
            Assert.That(_mail.Notices, Is.Empty,
                "a refusal decided by the username must not also do the address branch's work - "
                + "mail sent here would make the kept refusal measurable as an address oracle");
        });
    }

    [Test]
    public async Task TakenUsername_IsRefusedIdenticallyForAKnownAndAnUnknownAddress()
    {
        SeedUser("taken@example.com", "existing_user");

        var known = await HandleAsync(Request("taken@example.com", "existing_user"));
        var unknown = await HandleAsync(Request("nobody@example.com", "existing_user"));

        Assert.That(
            known.Failures.Select(f => (f.PropertyName, f.ErrorMessage)),
            Is.EquivalentTo(unknown.Failures.Select(f => (f.PropertyName, f.ErrorMessage))));
    }

    [Test]
    public async Task UnderageBirthDate_IsRefusedForARegisteredAddressToo()
    {
        // Left to ApplicationUser.Create (which only the create branch reaches), an underage signup
        // would be refused for a free address and silently accepted for a registered one.
        SeedUser("taken@example.com", "existing_user");

        var request = Request("taken@example.com", "someoneelse");
        request.BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-10));

        var (failures, bus) = await HandleAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Not.Empty);
            Assert.That(bus.Invoked, Is.Empty);
            Assert.That(_mail.Notices, Is.Empty);
        });
    }

    [Test]
    public async Task MissingAddress_IsRefusedRatherThanMatchingEveryAccountWithoutOne()
    {
        // A blank address normalises to null, and bot accounts have a null NormalizedEmail - so an
        // unguarded lookup would match one of them and answer 202 for an empty field.
        var (failures, bus) = await HandleAsync(Request("", "someoneelse"));

        Assert.Multiple(() =>
        {
            Assert.That(failures.Select(f => f.PropertyName), Does.Contain("Email"));
            Assert.That(bus.Invoked, Is.Empty);
            Assert.That(_mail.Notices, Is.Empty);
        });
    }
}
