namespace Identity.Contracts.Bus.Response;

/// <summary>The numbers, for the subset of requested accounts that have one.</summary>
public class GetUserPhoneNumbersResponse
{
    public IReadOnlyList<UserPhoneNumberDto> PhoneNumbers { get; set; } = [];

    /// <summary>
    /// Requested ids the handler refused to answer for because the batch was over
    /// <c>GetUserPhoneNumbersRequest.MaxUserIds</c>.
    /// </summary>
    public IReadOnlyList<string> OmittedUserIds { get; set; } = [];
}

/// <summary>One account's phone number.</summary>
public class UserPhoneNumberDto
{
    public string UserId { get; set; } = null!;

    /// <summary>E.164, normalised on write by <c>E164PhoneNumber</c>.</summary>
    public string PhoneNumber { get; set; } = null!;

    /// <summary>When the number was last written.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
