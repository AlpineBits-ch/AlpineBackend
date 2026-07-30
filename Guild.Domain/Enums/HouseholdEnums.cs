namespace Guild.Domain.Enums;

/// <summary>How an <c>Expense</c>'s total is divided across its participants.</summary>
public enum ExpenseSplitKind
{
    /// <summary>Same amount each; the remainder is distributed deterministically (see
    /// ExpenseSplitter).</summary>
    Equal,

    /// <summary>Weighted - "Anna counts double, she has the big room". ShareValue is the weight.</summary>
    Shares,

    /// <summary>Caller supplies each participant's exact minor-unit amount; they must sum to the
    /// expense total.</summary>
    Exact,
}

public enum DecisionStatus
{
    Open,

    /// <summary>Closed with a winning option.</summary>
    Decided,

    /// <summary>Closed with every option carrying an unresolved block. Deliberately distinct from
    /// Decided: "we couldn't agree" is information, not a null result.</summary>
    Blocked,

    Cancelled,

    /// <summary>ClosesAt passed without quorum.</summary>
    Expired,
}

/// <summary>Consent rather than majority. A single <see cref="Block"/> stops an option regardless
/// of how much support it has - that's the whole point of the module, and why a block must carry
/// a reason.</summary>
public enum DecisionVoteKind
{
    Support,
    Abstain,
    Block,
}

/// <summary>Ambient "who's home". Deliberately not called presence: GuildHydrateService's
/// presence already means online/offline, and conflating the two would be endless trouble.</summary>
public enum HomeStatusKind
{
    Home,
    Out,
    Asleep,
    DoNotDisturb,
    OnMyWay,
}
