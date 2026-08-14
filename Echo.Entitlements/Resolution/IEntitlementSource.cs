using Echo.Entitlements.Model;

namespace Echo.Entitlements.Resolution;

/// <summary>
/// One place entitlements can come from: the license mode, an admin grant, a promotion, a Stripe
/// subscription, a boost, the subject's plan.
/// </summary>
public interface IEntitlementSource
{
    /// <summary>Where this source sits in the order of spec section 4.2. Lower wins.</summary>
    EntitlementPrecedence Precedence { get; }

    /// <summary>True when nothing below this source should be consulted at all.</summary>
    bool ShortCircuits => false;

    /// <summary>What this source has to say about one subject.</summary>
    Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken cancellationToken);
}
