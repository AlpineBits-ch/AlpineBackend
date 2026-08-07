namespace Guild.Contracts;

/// <summary>The localization keys household notifications are written in.</summary>
public static class HouseholdLocKeys
{
    // ── Chores ───────────────────────────────────────────────────────────────

    /// <summary>"Your turn, and it's due now."</summary>
    public const string ChoreDueBody = "household_chore_due_body";

    /// <summary>"Still waiting on this one at home." - a nudge.</summary>
    public const string ChoreNudgeBody = "household_chore_nudge_body";

    /// <summary>"Picked up from a flatmate who's away." - sent to whoever inherited a chore when
    /// its assignee went on holiday, so the reassignment is not a silent surprise.</summary>
    public const string ChoreReassignedBody = "household_chore_reassigned_body";

    // ── Ledger ───────────────────────────────────────────────────────────────

    /// <summary>"Your share is {0}." - {0} is a preformatted money string.</summary>
    public const string ExpenseShareBody = "household_expense_share_body";

    /// <summary>"Recorded as paid by you: {0}."</summary>
    public const string ExpensePaidBody = "household_expense_paid_body";

    /// <summary>"Payment received"</summary>
    public const string SettlementReceivedTitle = "household_settlement_received_title";

    /// <summary>"{0} was recorded as paid to you."</summary>
    public const string SettlementReceivedBody = "household_settlement_received_body";

    /// <summary>"Payment recorded"</summary>
    public const string SettlementRecordedTitle = "household_settlement_recorded_title";

    /// <summary>"{0} was recorded as paid by you."</summary>
    public const string SettlementRecordedBody = "household_settlement_recorded_body";

    // ── Bills ────────────────────────────────────────────────────────────────

    /// <summary>"Due today. Your share is {0}." - {0} is a preformatted money string.</summary>
    public const string BillDueBody = "household_bill_due_body";

    /// <summary>"Due in {0}.</summary>
    public const string BillDueSoonBody = "household_bill_due_soon_body";

    /// <summary>"Added to the ledger.</summary>
    public const string BillPostedBody = "household_bill_posted_body";

    /// <summary>"A bill needs an amount before it can be split." - a variable bill that has come
    /// due without anybody entering what it actually was.</summary>
    public const string BillNeedsAmountBody = "household_bill_needs_amount_body";

    // ── Meals ────────────────────────────────────────────────────────────────

    /// <summary>"You're down to cook" - used as the title only when the plan entry names no
    /// recipe and carries no free text of its own.</summary>
    public const string MealCookingTodayTitle = "household_meal_cooking_today_title";

    /// <summary>"You're down to cook this today."</summary>
    public const string MealCookingTodayBody = "household_meal_cooking_today_body";

    // ── Maintenance ──────────────────────────────────────────────────────────

    /// <summary>"This is due for a service."</summary>
    public const string MaintenanceDueBody = "household_maintenance_due_body";

    /// <summary>"The warranty runs out in {0}." - {0} is a preformatted duration.</summary>
    public const string MaintenanceWarrantyBody = "household_maintenance_warranty_body";

    /// <summary>"Marked as broken. Don't use it for now."</summary>
    public const string MaintenanceBrokenBody = "household_maintenance_broken_body";

    // ── Decisions ────────────────────────────────────────────────────────────

    /// <summary>"A house decision needs your vote."</summary>
    public const string DecisionOpenedBody = "household_decision_opened_body";

    /// <summary>"A house decision needs your vote before it closes."</summary>
    public const string DecisionOpenedClosingBody = "household_decision_opened_closing_body";

    /// <summary>"Blocked: {0}" - {0} is the blocker's own words, already truncated.</summary>
    public const string DecisionBlockedBody = "household_decision_blocked_body";

    // ── Shopping lists ───────────────────────────────────────────────────────

    /// <summary>"Just went on {0} - could you grab it while you're out?" - {0} is the list's
    /// channel name.</summary>
    public const string ListItemAddedBody = "household_list_item_added_body";

    /// <summary>"You've been asked to pick this up from {0}."</summary>
    public const string ListItemAssignedBody = "household_list_item_assigned_body";

    /// <summary>"Everything on it is ticked off."</summary>
    public const string ListCompletedBody = "household_list_completed_body";

    // ── Pantry ───────────────────────────────────────────────────────────────

    /// <summary>"Running low at home."</summary>
    public const string PantryLowBody = "household_pantry_low_body";

    /// <summary>"Running low, so it's gone on {0}." - {0} is the restock list's channel name.</summary>
    public const string PantryLowListedBody = "household_pantry_low_listed_body";

    /// <summary>"Ran low at home and went on the list - could you grab it while you're out?"</summary>
    public const string PantryRestockBody = "household_pantry_restock_body";

    /// <summary>"Use it or lose it"</summary>
    public const string PantryExpiringTitle = "household_pantry_expiring_title";

    /// <summary>"{0} is about to go off."</summary>
    public const string PantryExpiringOneBody = "household_pantry_expiring_one_body";

    /// <summary>"{0} and {1} are about to go off."</summary>
    public const string PantryExpiringTwoBody = "household_pantry_expiring_two_body";

    /// <summary>"{0}, {1} and {2} more are about to go off." - {2} is a count, formatted by the
    /// server so the client never has to know it is a number.</summary>
    public const string PantryExpiringManyBody = "household_pantry_expiring_many_body";

    // ── Hidden content ───────────────────────────────────────────────────────

    /// <summary>"Home" - the title a recipient with HidePushContent gets instead of the real one
    /// (privacy spec T2-23). Localized like everything else: hiding the content is not a reason to
    /// switch the reader back into English.</summary>
    public const string HiddenTitle = "household_hidden_title";

    /// <summary>"Something needs your attention at home"</summary>
    public const string HiddenBody = "household_hidden_body";

    /// <summary>Every key this service is allowed to send.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ChoreDueBody,
        ChoreNudgeBody,
        ChoreReassignedBody,
        BillDueBody,
        BillDueSoonBody,
        BillPostedBody,
        BillNeedsAmountBody,
        MealCookingTodayTitle,
        MealCookingTodayBody,
        MaintenanceDueBody,
        MaintenanceWarrantyBody,
        MaintenanceBrokenBody,
        ExpenseShareBody,
        ExpensePaidBody,
        SettlementReceivedTitle,
        SettlementReceivedBody,
        SettlementRecordedTitle,
        SettlementRecordedBody,
        DecisionOpenedBody,
        DecisionOpenedClosingBody,
        DecisionBlockedBody,
        ListItemAddedBody,
        ListItemAssignedBody,
        ListCompletedBody,
        PantryLowBody,
        PantryLowListedBody,
        PantryRestockBody,
        PantryExpiringTitle,
        PantryExpiringOneBody,
        PantryExpiringTwoBody,
        PantryExpiringManyBody,
        HiddenTitle,
        HiddenBody,
    };
}
