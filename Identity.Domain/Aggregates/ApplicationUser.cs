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
    
    public EncryptedMasterKey? EncryptedMasterKey{ get; set; }
    
    public ICollection<UserDeviceToken> DeviceTokens { get; set; } = new List<UserDeviceToken>();
    public ICollection<UserVoipToken> VoipTokens { get; set; } = new List<UserVoipToken>();
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

    /// <summary>
    /// Anonymizes the account in place rather than deleting the row: every other service
    /// (Guild.GuildMember, Messaging.Message.AuthorId, Guild.GuildAuditLogEntry.ActorUserId, etc.)
    /// references this Id by pointer and resolves display data live rather than storing its own
    /// copy, so scrubbing this one row is what makes "Deleted User" show up everywhere those
    /// references still exist - the same mechanism Discord uses.
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