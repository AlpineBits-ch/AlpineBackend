namespace Identity.Contracts.Bus.Events;

/// <summary>An account removed its phone number.</summary>
public class UserPhoneNumberRemovedEvent
{
    public string UserId { get; set; } = null!;
}
