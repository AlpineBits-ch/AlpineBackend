namespace Identity.Application.Dtos.Request;

/// <summary>The body of <c>PUT api/v1/users/self/phone</c>.</summary>
public class SetPhoneNumberDto
{
    /// <summary>The number in E.164, e.g. <c>+41791234567</c>.</summary>
    public string? PhoneNumber { get; set; }
}
