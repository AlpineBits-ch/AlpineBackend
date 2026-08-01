using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Domain.Events.User;
using Identity.Domain.ValueObjects;
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


public class ApplicationUser : IdentityUser<string>, IEventSource, IPrefixedEntity, IBaseEntity
{
    [NotMapped]
    private readonly Lock _lock = new();

    public DateOnly BirthDate { get; set; }
    
    public DateTimeOffset? PhoneVerifiedAt { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }

    public AgeVerification AgeVerification { get; set; } = null!;
    
    public UserPreferences UserPreferences { get; set; } = new();
    
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
            AgeVerification = AgeVerification.CreateInitial(createUserParams.BirthDate),
            Status = UserStatus.Active,
            UserPreferences = new UserPreferences()
            {
                Id = UserPreferences.GenerateId(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Data = "{}",
                DirectMessageSettings = DirectMessageSettings.FilterNonFriends,
                PrivacySettings = PrivacySettings.None,
            }
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
            SecurityStamp = Guid.NewGuid().ToString(),
            Status = UserStatus.Active,
            AgeVerification = new AgeVerification
            {
                Level = AgeVertificationLevel.None,
            },
            UserPreferences = new UserPreferences
            {
                Id = UserPreferences.GenerateId(),
                CreatedAt = date,
                UpdatedAt = date,
                Data = "{}",
                DirectMessageSettings = DirectMessageSettings.FilterNonFriends,
                PrivacySettings = PrivacySettings.None,
            },
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

    /// <summary>
    /// Anonymizes the account in place rather than deleting the row: every other service
    /// (Guild.GuildMember, Messaging.Message.AuthorId, Guild.GuildAuditLogEntry.ActorUserId,
    /// etc.) references this Id by pointer and resolves display data live rather than storing
    /// its own copy, so scrubbing this one row is what makes "Deleted User" show up everywhere
    /// those references still exist - the same mechanism Discord uses. Idempotent so a
    /// redelivered PurgeUserDataCommand is safe.
    /// </summary>
    public void Tombstone()
    {
        if (Status == UserStatus.Deleted) return;

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
        Status = UserStatus.Deleted;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
    [NotMapped] public static string Prefix { get; } = "user_"; // Explicitly handled here to not do as many allocs.
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    private static string GenerateId()
    {
        
        return Ksuid.Generate(Prefix);
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