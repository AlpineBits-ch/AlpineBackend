namespace Federation.Domain.Aggregates;

public enum AcceptancePolicy
{
    AutoAccept = 0,
    RequireApproval = 1,
}

public class FederationSettings
{
    public const string SingletonId = "fedc_default";
    public string Id { get; set; } = SingletonId;
    public AcceptancePolicy AcceptancePolicy { get; set; } = AcceptancePolicy.AutoAccept;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
