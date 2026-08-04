using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Domain.Events.User;
using Identity.Domain.ValueObjects;
using Ids;
using Microsoft.AspNetCore.Identity;
using Persistence;

namespace Identity.Domain.Aggregates;


public class CreateUserParams
{
    public string Email { get; init; }
    public string PhoneNumber { get; init; }
    public string Username { get; init; }
    
    public DateOnly BirthDate { get; init; }
    public string? Bio { get; init; }
}


/// <summary>
/// The deployment-level choices <see cref="ApplicationUser.Tombstone"/> needs. Passed in rather than
/// read from <c>AppEnvironment.Env</c> because Identity.Domain does not reference it - and should
/// not: a domain aggregate that reads process environment variables cannot be tested at two
/// different configurations in the same run.
/// </summary>
public class TombstoneOptions
{
    /// <summary>Age at which the account counted as an adult, for the retained boolean only.</summary>
    public int AgeOfMajority { get; init; } = 18;

    /// <summary>
    /// Whether to keep <see cref="ApplicationUser.WasVerifiedAdult"/> after the purge.
    ///
    /// <para>True by default. A single boolean, unlinkable to a person on its own, is the minimum a
    /// deployment needs to answer "was this account an adult" if that is ever contested - and the
    /// alternative to keeping it is keeping the birth date, which is what T1-9 is removing. Set false
    /// where even that is unwanted; nothing in this codebase reads it.</para>
    /// </summary>
    public bool RetainWasVerifiedAdult { get; init; } = true;
}

public class ApplicationUser : IdentityUser<string>, IEventSource, IPrefixedEntity, IBaseEntity
{
    [NotMapped]
    private readonly Lock _lock = new();

    public DateOnly BirthDate { get; set; }
    
    public DateTimeOffset? PhoneVerifiedAt { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }

    public AgeVerification AgeVerification { get; set; } = null!;

    /// <summary>
    /// The one non-identifying bit of the age record that survives a purge (T1-9): whether the
    /// account had been verified as an adult at the moment it was tombstoned.
    ///
    /// <para>Null on every live account - it is written only by <see cref="Tombstone"/>, from the
    /// birth date, immediately before that birth date is destroyed. It exists because "we deleted
    /// everything, including any evidence that this account was ever confirmed to be an adult" is a
    /// bad position to be in when the question later arises, and a single boolean cannot re-identify
    /// anyone. A deployment that does not want even this can set
    /// <c>PRIVACY_RETAIN_WAS_VERIFIED_ADULT=false</c> and it is left null.</para>
    /// </summary>
    public bool? WasVerifiedAdult { get; set; }

    public UserPreferences UserPreferences { get; set; } = new();

    /// <summary>
    /// The account's privacy record - one row, own table, keyed the other way round from
    /// <see cref="UserPreferences"/> (the FK lives on <see cref="Entities.UserPrivacySettings.UserId"/>,
    /// not here).
    ///
    /// <para>Nullable on the navigation because rows created before the <c>AddUserPrivacySettings</c>
    /// migration's backfill, or loaded without an Include, genuinely have nothing here - and a
    /// non-null type would turn "not loaded" into a lie. Readers that need the settings resolve them
    /// through the privacy-settings endpoint or the bus handler, both of which materialize defaults
    /// for an account that somehow has no row.</para>
    /// </summary>
    public UserPrivacySettings? UserPrivacySettings { get; set; }


    public UserStatus Status { get; set; } = UserStatus.Active;
    
    public virtual ICollection<UserKey> UserKeys { get; set; } = new List<UserKey>();
    public string UserPreferencesId { get; set; } = null!;
    
    public string? Bio { get; set; }
    public string? FederatedServerId { get; set; }

    public string JsonSettings { get; set; } = "{}";
    
    /// <summary>
    /// The account master key wrapped under a key derived from the <b>password</b>.
    ///
    /// <para>Historically the only wrapping, which made a password reset silently destroy every
    /// backup blob and the account identity key: the envelope stays sealed under Argon2(old
    /// password), and a reset is by definition the case where the user no longer has that password.
    /// It is now one of two - see <see cref="RecoveryCodeWrappedMasterKey"/> - and a reset
    /// invalidates only this one.</para>
    /// </summary>
    public EncryptedMasterKey? EncryptedMasterKey{ get; set; }

    /// <summary>
    /// The same master key wrapped under a key derived from the <b>recovery code</b>.
    ///
    /// <para>The only credential that survives a password reset, which is why it is required to
    /// enter <see cref="Domain.ProtectionLevel.VerifiedDevices"/> and to put engine state in the
    /// cloud, and why every account should be pushed to save one. Losing both the password and the
    /// recovery code is genuinely unrecoverable - that has to be said in the UI at setup time, not
    /// discovered at restore time.</para>
    /// </summary>
    public EncryptedMasterKey? RecoveryCodeWrappedMasterKey { get; set; }

    /// <summary>
    /// When a password reset made <see cref="EncryptedMasterKey"/> undecryptable.
    ///
    /// <para>Set by the reset path, cleared when the client re-wraps under the new password. While
    /// it is set, the password no longer opens the master key and only the recovery code does - so
    /// this is what turns "your backups silently stopped working" into something the server can tell
    /// the client to fix on next unlock.</para>
    /// </summary>
    public DateTimeOffset? MasterKeyPasswordWrappingInvalidatedAt { get; set; }

    /// <summary>
    /// Whether any credential the user could still have will open the master key.
    ///
    /// <para>False means the encrypted history is gone: the password wrapping was invalidated by a
    /// reset and there is no recovery-code wrapping to fall back on. Reported to the user as exactly
    /// that, rather than left to appear to work until the first restore attempt fails.</para>
    /// </summary>
    [NotMapped]
    public bool EncryptedHistoryRecoverable =>
        EncryptedMasterKey is null
        || RecoveryCodeWrappedMasterKey is not null
        || MasterKeyPasswordWrappingInvalidatedAt is null;

    /// <summary>
    /// How this account admits new devices to its encrypted conversations. See
    /// <see cref="Domain.ProtectionLevel"/>.
    ///
    /// <para><b>This column is a cache, not the authority.</b> If it were the authority, a hostile
    /// server could flip a strict account to permissive and then auto-admit a device it controls -
    /// which is exactly the power the strict tier exists to remove. The authority is
    /// <see cref="ProtectionLevelAssertion"/>, signed by the user's identity key; clients enforce the
    /// last validly-signed level they have seen and fail closed to the stricter reading when they
    /// cannot verify one. The column exists so the server can answer "may this join request be
    /// auto-admitted" without asking a client, and being wrong about it costs nothing a client will
    /// act on.</para>
    /// </summary>
    public ProtectionLevel ProtectionLevel { get; set; } = ProtectionLevel.TrustedSignIn;

    /// <summary>The signed assertion clients actually verify. Opaque here - the server never
    /// produces one and cannot, which is the whole point.</summary>
    public byte[]? ProtectionLevelAssertion { get; set; }

    /// <summary>Monotonic. Guards against a replayed old assertion being accepted as current, and
    /// against two devices racing to change the level.</summary>
    public int ProtectionLevelVersion { get; set; }

    public DateTimeOffset? ProtectionLevelUpdatedAt { get; set; }

    /// <summary>
    /// Public half of the account's long-lived Ed25519 identity key, generated client-side at signup.
    ///
    /// <para>This is what makes full-loss recovery possible without a server-side backdoor. Every
    /// device carries a certificate signed by the private half, so a peer can verify offline that a
    /// device really belongs to this account - with none of the account's devices online, and
    /// without taking the server's word for it. The server never holds the private half (it is
    /// wrapped under the recovery key inside the backup envelope), so the server cannot mint a
    /// certificate for a device it injected.</para>
    ///
    /// <para>Peers TOFU-pin this exactly like a Signal safety number. Changing it therefore
    /// invalidates every peer's pinning, which is why rotation is a security event with its own
    /// ceremony rather than a routine write.</para>
    /// </summary>
    public byte[]? AccountIdentityPublicKey { get; set; }

    /// <summary>Monotonic. Lets a peer detect a rollback to a superseded identity key.</summary>
    public int AccountIdentityKeyVersion { get; set; }

    /// <summary>The new key signed by the <i>outgoing</i> one, when the outgoing one still existed.
    /// Peers verify continuity from this automatically; a rotation without it is a
    /// safety-number-changed warning that has to be resolved out of band, never auto-accepted.</summary>
    public byte[]? AccountIdentityKeyRotationSignature { get; set; }

    public DateTimeOffset? AccountIdentityKeyUpdatedAt { get; set; }
    
    public ICollection<UserPushToken> PushTokens { get; set; } = new List<UserPushToken>();
    public ICollection<UserDevice> Devices { get; set; } = new List<UserDevice>();
    public ICollection<UserKeyPackage> KeyPackages { get; set; } = new List<UserKeyPackage>();
    public ICollection<UserDeviceBackup> Backups { get; set; } = new List<UserDeviceBackup>();
    
    public string? SteamId { get; set; }

    /// <summary>
    /// When this account answered the onboarding picker, or null if it never has.
    ///
    /// <para>Null is the whole trigger: the client shows the picker on exactly this condition, and
    /// the rest of its launch sequence waits on the answer. Stamped once and never re-stamped, so
    /// re-running the picker from settings changes <see cref="Interests"/> without falsifying when
    /// the account was first onboarded.</para>
    /// </summary>
    public DateTimeOffset? OnboardedAt { get; set; }

    /// <summary>
    /// Which halves of the product this account signed up for.
    ///
    /// <para>Decides whether the client runs master-key setup at launch or defers it until the
    /// account first reaches for something social. Meaningless while <see cref="OnboardedAt"/> is
    /// null, which is why the two are always written together.</para>
    /// </summary>
    public UserInterests Interests { get; set; } = UserInterests.None;

    public UserType UserType { get; set; } = UserType.Default;

    public DateTimeOffset? DeletionRequestedAt { get; set; }

    /// <summary>When the grace period ends and AccountDeletionPurgeSweepService is allowed to
    /// kick off the cross-service purge. Null unless Status is PendingDeletion or
    /// PurgeInProgress.</summary>
    public DateTimeOffset? PurgeScheduledAt { get; set; }

    public static ApplicationUser Create(CreateUserParams createUserParams)
    {
        var id = GenerateId();
        var user = new ApplicationUser()
        {
            Email = new Email(createUserParams.Email).Value,
            NormalizedEmail = createUserParams.Email.ToUpperInvariant(),
            PhoneNumber = createUserParams.PhoneNumber,
            UserName = createUserParams.Username,
            BirthDate = createUserParams.BirthDate,
            Bio = createUserParams.Bio,
            NormalizedUserName = createUserParams.Username.ToUpperInvariant(),
            CorrelationId = id,
            Id = id,
            UserType = UserType.Default,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SecurityStamp = Guid.NewGuid().ToString(),
            // Users are persisted with ctx.Users.Add rather than UserManager.CreateAsync, and
            // UserManager.CreateAsync is the only place the framework would set this. Without it
            // the flag stays false, UserManager.IsLockedOutAsync short-circuits to false, and
            // SignInManager.CheckPasswordSignInAsync(..., lockoutOnFailure: true) can never return
            // LockedOut - so every lockout-aware check in this service (including the password
            // gates in front of device binding and key-material operations) was inert while
            // AccessFailedCount and LockoutEnd were still being written.
            LockoutEnabled = true,
            AgeVerification = AgeVerification.CreateInitial(createUserParams.BirthDate),
            Status = UserStatus.Active,
            // The legacy block. Still written because shipped clients read it off GET /users/self;
            // nothing enforces it any more - see UserPrivacySettings below and UserPreferences'
            // [Obsolete] members.
#pragma warning disable CS0618
            UserPreferences = new UserPreferences()
            {
                Id = UserPreferences.GenerateId(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Data = "{}",
                DirectMessageSettings = DirectMessageSettings.FilterNonFriends,
                PrivacySettings = PrivacySettings.None,
            },
#pragma warning restore CS0618
            // Minted here rather than lazily on first read: a missing owned/related row is the
            // failure mode User.Tests/ApplicationUserBotTests already exists to pin, and a privacy
            // record that materializes on demand would mean the very first policy resolution for a
            // new account races the write that creates it.
            UserPrivacySettings = UserPrivacySettings.CreateDefault(id, DateTimeOffset.UtcNow),
        };

        user.AddDomainEvent(new UserCreated()
        {
            UserId = id,
            Email = user.Email,
            // We pass username and bio to make life easier for the integration event. We dont save it here
            UserName = createUserParams.Username,
            Bio = user.Bio,
            CorrelationId = user.CorrelationId,
        });

        return user;
        
    }

    /// <summary>
    /// Creates a bot account. The id is caller-supplied (not self-generated) so that the
    /// Bots service can mint it up front and safely retry the create-application flow if
    /// the cross-service write partially fails. Skips email/age-verification/welcome-email
    /// machinery entirely - none of that applies to a non-human account.
    /// </summary>
    public static ApplicationUser CreateBot(string botUserId, string name)
    {
        var date = DateTime.UtcNow;
        return new ApplicationUser
        {
            Id = botUserId,
            CorrelationId = botUserId,
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            UserType = UserType.Bot,
            CreatedAt = date,
            UpdatedAt = date,
            // Stamped so a bot never trips the onboarding picker. Nothing signs into a bot account
            // interactively, so a null here would be a gate with nobody on the far side of it -
            // and the bot half of the client would sit behind a screen it can never answer.
            OnboardedAt = date,
            Interests = UserInterests.Social,
            SecurityStamp = Guid.NewGuid().ToString(),
            Status = UserStatus.Active,
            // See ApplicationUser.Create - lockout has to be enabled explicitly because these rows
            // never go through UserManager.CreateAsync.
            LockoutEnabled = true,
            AgeVerification = new AgeVerification
            {
                Level = AgeVertificationLevel.None,
            },
#pragma warning disable CS0618
            UserPreferences = new UserPreferences
            {
                Id = UserPreferences.GenerateId(),
                CreatedAt = date,
                UpdatedAt = date,
                Data = "{}",
                DirectMessageSettings = DirectMessageSettings.FilterNonFriends,
                PrivacySettings = PrivacySettings.None,
            },
#pragma warning restore CS0618
            // A bot gets the same defaults a human does. It is a real account with real reachability
            // - "who may DM this bot" is a question with an answer - and leaving the row absent would
            // make every policy resolution against a bot fall through to the fail-closed path.
            UserPrivacySettings = UserPrivacySettings.CreateDefault(botUserId, date),
        };
    }

    public void SetPasswordHash(string passwordHash)
    {
        this.PasswordHash = passwordHash;
    }

    public bool IsSigninAllowed()
    {
        return Status == UserStatus.Active;
    }

    /// <summary>Starts the grace-period countdown. Reversible via CancelDeletionRequest until
    /// the sweep flips status to PurgeInProgress.</summary>
    public void RequestDeletion(DateTimeOffset purgeScheduledAt)
    {
        Status = UserStatus.PendingDeletion;
        DeletionRequestedAt = DateTimeOffset.UtcNow;
        PurgeScheduledAt = purgeScheduledAt;
    }

    /// <summary>No-ops (returns false) once the purge has already started - by that point the
    /// cross-service fan-out is underway and can't be safely unwound.</summary>
    public bool CancelDeletionRequest()
    {
        if (Status != UserStatus.PendingDeletion) return false;

        Status = UserStatus.Active;
        DeletionRequestedAt = null;
        PurgeScheduledAt = null;
        return true;
    }

    /// <summary>Marks the account as no longer cancellable, right before the sweep publishes
    /// AccountPurgeStartedEvent - guards against the next sweep tick re-publishing while the
    /// AccountDeletionSaga fan-out is still in flight.</summary>
    public void BeginPurge()
    {
        Status = UserStatus.PurgeInProgress;
    }

    /// <summary>Whether this account is below the age of majority right now, derived live from
    /// <see cref="AgeVerification"/>'s birth date. See <see cref="AgeVerification.IsMinorAt"/> for
    /// why nothing caches it.</summary>
    public bool IsMinorAt(DateTimeOffset now, int ageOfMajority) =>
        AgeVerification?.IsMinorAt(now, ageOfMajority) ?? false;

    /// <summary>
    /// Anonymizes the account in place rather than deleting the row: every other service
    /// (Guild.GuildMember, Messaging.Message.AuthorId, Guild.GuildAuditLogEntry.ActorUserId,
    /// etc.) references this Id by pointer and resolves display data live rather than storing
    /// its own copy, so scrubbing this one row is what makes "Deleted User" show up everywhere
    /// those references still exist - the same mechanism Discord uses. Idempotent so a
    /// redelivered PurgeUserDataCommand is safe.
    ///
    /// <para><b>The age data goes too (T1-9).</b> This used to leave <see cref="BirthDate"/> and the
    /// whole <see cref="AgeVerification"/> value object standing - a date of birth plus the dates on
    /// which self-declaration, AI estimation and government-ID verification were completed, all still
    /// bound to the account id, on a row whose entire purpose is that it has been erased. A birth
    /// date is one of the highest-value re-identification keys there is; leaving it made the
    /// tombstone a rename rather than an erasure. What may survive is a single non-identifying
    /// boolean, and only when the deployment asks for it.</para>
    /// </summary>
    /// <param name="options">Age-of-majority and retention choices for the one bit that may be kept.
    /// Defaults erase everything except <see cref="WasVerifiedAdult"/>.</param>
    public void Tombstone(TombstoneOptions? options = null)
    {
        if (Status == UserStatus.Deleted) return;

        options ??= new TombstoneOptions();

        var suffix = Id.Length >= 6 ? Id[^6..] : Id;
        UserName = $"Deleted User {suffix}";
        NormalizedUserName = UserName.ToUpperInvariant();
        Email = null;
        NormalizedEmail = null;
        PhoneNumber = null;
        PhoneVerifiedAt = null;
        EmailVerifiedAt = null;
        Bio = null;
        PasswordHash = null;
        SecurityStamp = Guid.NewGuid().ToString();
        SteamId = null;
        JsonSettings = "{}";
        EncryptedMasterKey = null;
        DeletionRequestedAt = null;
        PurgeScheduledAt = null;

        // Read before the erase, obviously - and only from a birth date that was actually recorded.
        // An account with no birth date (a bot) yields null rather than false, because "we have no
        // evidence either way" and "we have evidence they were a minor" are different claims and
        // only one of them is true here.
        var age = AgeVerification?.AgeInYearsAt(DateTimeOffset.UtcNow);
        WasVerifiedAdult = options.RetainWasVerifiedAdult && age is not null
            ? age >= options.AgeOfMajority
            : null;

        BirthDate = default;
        AgeVerification?.Purge();

        Status = UserStatus.Deleted;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
    [NotMapped] public static string Prefix { get; } = "user";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Derives from IdentityUser rather than BaseEntity, so the generator has to be
    /// forwarded to by hand rather than inherited.</summary>
    private static string GenerateId()
    {
        return Identifier.New(Prefix);
    }

    public IReadOnlyCollection<DomainEvent> GetDomainEvents()
    {
        return DomainEvents;
    }

   

    public string CorrelationId { get; set; }
    
    [NotMapped]
    private readonly List<DomainEvent> _domainEvents = new();
    private IReadOnlyCollection<DomainEvent> DomainEvents => _domainEvents.ToList().AsReadOnly();

    public void AddDomainEvent(DomainEvent domainEvent)
    {
        lock (_lock)
        {
            domainEvent.CorrelationId = CorrelationId;
            _domainEvents.Add(domainEvent);
        }
        
    }
}