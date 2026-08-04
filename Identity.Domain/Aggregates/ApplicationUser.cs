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


/// <summary>The deployment-level choices <see cref="ApplicationUser.Tombstone"/> needs.</summary>
public class TombstoneOptions
{
    /// <summary>Age at which the account counted as an adult, for the retained boolean only.</summary>
    public int AgeOfMajority { get; init; } = 18;

    /// <summary>
    /// Whether to keep <see cref="ApplicationUser.WasVerifiedAdult"/> after the purge.
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
    /// </summary>
    public bool? WasVerifiedAdult { get; set; }

    public UserPreferences UserPreferences { get; set; } = new();

    /// <summary>
    /// The account's privacy record - one row, own table, keyed the other way round from <see
    /// cref="UserPreferences"/> (the FK lives on <see cref="Entities.UserPrivacySettings.UserId"/>,
    /// not here).
    /// </summary>
    public UserPrivacySettings? UserPrivacySettings { get; set; }


    public UserStatus Status { get; set; } = UserStatus.Active;
    
    public virtual ICollection<UserKey> UserKeys { get; set; } = new List<UserKey>();
    public string UserPreferencesId { get; set; } = null!;
    
    public string? Bio { get; set; }
    public string? FederatedServerId { get; set; }

    public string JsonSettings { get; set; } = "{}";
    
    /// <summary>The account master key wrapped under a key derived from the password.</summary>
    public EncryptedMasterKey? EncryptedMasterKey{ get; set; }

    /// <summary>The same master key wrapped under a key derived from the recovery code.</summary>
    public EncryptedMasterKey? RecoveryCodeWrappedMasterKey { get; set; }

    /// <summary>
    /// When a password reset made <see cref="EncryptedMasterKey"/> undecryptable.
    /// </summary>
    public DateTimeOffset? MasterKeyPasswordWrappingInvalidatedAt { get; set; }

    /// <summary>
    /// Whether any credential the user could still have will open the master key.
    /// </summary>
    [NotMapped]
    public bool EncryptedHistoryRecoverable =>
        EncryptedMasterKey is null
        || RecoveryCodeWrappedMasterKey is not null
        || MasterKeyPasswordWrappingInvalidatedAt is null;

    /// <summary>How this account admits new devices to its encrypted conversations.</summary>
    public ProtectionLevel ProtectionLevel { get; set; } = ProtectionLevel.TrustedSignIn;

    /// <summary>The signed assertion clients actually verify.</summary>
    public byte[]? ProtectionLevelAssertion { get; set; }

    /// <summary>Monotonic.</summary>
    public int ProtectionLevelVersion { get; set; }

    public DateTimeOffset? ProtectionLevelUpdatedAt { get; set; }

    /// <summary>
    /// Public half of the account's long-lived Ed25519 identity key, generated client-side at
    /// signup.
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
    /// </summary>
    public DateTimeOffset? OnboardedAt { get; set; }

    /// <summary>Which halves of the product this account signed up for.</summary>
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
            // UserManager.CreateAsync is the only place the framework would set this.
            LockoutEnabled = true,
            AgeVerification = AgeVerification.CreateInitial(createUserParams.BirthDate),
            Status = UserStatus.Active,
            // The legacy block.
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

    /// <summary>Creates a bot account.</summary>
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
            // Stamped so a bot never trips the onboarding picker.
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
            // A bot gets the same defaults a human does.
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

    /// <summary>Starts the grace-period countdown.</summary>
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
    /// (Guild.GuildMember, Messaging.Message.AuthorId, Guild.GuildAuditLogEntry.ActorUserId, etc.)
    /// references this Id by pointer and resolves display data live rather than storing its own
    /// copy, so scrubbing this one row is what makes "Deleted User" show up everywhere those
    /// references still exist - the same mechanism Discord uses.
    /// </summary>
    /// <param name="options">
    /// Age-of-majority and retention choices for the one bit that may be kept.
    /// </param>
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