using Federation.Contracts.Materialization.Messaging;

namespace Federation.Application.Providers;

/// <summary>
/// The display identity a message travels under, kept together so the messaging provider methods do
/// not grow three loose parameters each.
/// </summary>
/// <param name="DisplayName">Name the message was rendered under, or null for a plain user post.</param>
/// <param name="AvatarUrl">Avatar that goes with <paramref name="DisplayName"/>.</param>
/// <param name="AuthorIdType">What kind of author spoke.</param>
public sealed record FederatedAuthorDisplay(
    string? DisplayName,
    string? AvatarUrl,
    FederatedAuthorIdType AuthorIdType)
{
    /// <summary>A message spoken by the account itself, with no overrides.</summary>
    public static FederatedAuthorDisplay RealUser { get; } = new(null, null, FederatedAuthorIdType.User);
}
