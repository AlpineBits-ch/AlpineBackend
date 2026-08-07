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

    /// <summary>Closed with every option carrying an unresolved block.</summary>
    Blocked,

    Cancelled,

    /// <summary>ClosesAt passed without quorum.</summary>
    Expired,
}

/// <summary>Consent rather than majority.</summary>
public enum DecisionVoteKind
{
    Support,
    Abstain,
    Block,
}

/// <summary>Ambient "who's home".</summary>
public enum HomeStatusKind
{
    Home,
    Out,
    Asleep,
    DoNotDisturb,
    OnMyWay,
}

/// <summary>The step a <c>RecurringExpense</c> takes between due dates.</summary>
public enum RecurrenceUnit
{
    Day,
    Week,
    Month,
    Year,
}

/// <summary>Where one generated instance of a recurring bill has got to.</summary>
public enum BillStatus
{
    /// <summary>Generated and waiting.</summary>
    Pending,

    /// <summary>Turned into a real <c>Expense</c>; <c>ExpenseId</c> points at it.</summary>
    Posted,

    /// <summary>Deliberately not charged this period - the flat was empty in August, the landlord
    /// waived it. Distinct from deleting the template, which would lose the next one too.</summary>
    Skipped,
}

/// <summary>What an expense was for.</summary>
public enum ExpenseCategory
{
    Uncategorized,
    Groceries,
    Rent,
    Utilities,
    Internet,
    Household,
    Transport,
    EatingOut,
    Entertainment,
    Health,
    Pets,
    Repairs,
    Other,
}

/// <summary>Which meal of the day a plan entry is for.</summary>
public enum MealSlot
{
    Breakfast,
    Lunch,
    Dinner,
    Other,
}

/// <summary>What state a piece of household equipment is in.</summary>
public enum AssetStatus
{
    Ok,
    NeedsAttention,
    Broken,
    OutOfService,
}

// There is deliberately no PaymentHandleKind here.
