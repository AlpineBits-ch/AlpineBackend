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

    public string JsonSettings { get; set; } = "{}";
    
    
    public EncryptedMasterKey? EncryptedMasterKey{ get; set; }
    
    public ICollection<UserDeviceToken> DeviceTokens { get; set; } = new List<UserDeviceToken>();
    public ICollection<UserDevice> Devices { get; set; } = new List<UserDevice>();
    public ICollection<UserKeyPackage> KeyPackages { get; set; } = new List<UserKeyPackage>();
    public ICollection<UserDeviceBackup> Backups { get; set; } = new List<UserDeviceBackup>();
    public static ApplicationUser Create(CreateUserParams createUserParams)
    {
        var id = GenerateId();
        var user = new ApplicationUser()
        {
            Email = new Email(createUserParams.Email).Value,
            NormalizedEmail = createUserParams.Email.ToUpperInvariant(),
            PhoneNumber = createUserParams.PhoneNumber,
            UserName = createUserParams.Email,
            BirthDate = createUserParams.BirthDate,
            Bio = createUserParams.Bio,
            NormalizedUserName = createUserParams.Email.ToUpperInvariant(),
            CorrelationId = id,
            Id = id,
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

    public void SetPasswordHash(string passwordHash)
    {
        this.PasswordHash = passwordHash;
    }

    public bool IsSigninAllowed()
    {
        return Status == UserStatus.Active;
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