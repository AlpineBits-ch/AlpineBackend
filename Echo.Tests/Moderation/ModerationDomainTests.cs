using Echo.Domain.Entities.Moderation;
using Echo.Domain.Enums;

namespace Echo.Tests.Moderation;

/// <summary>The moderation rules that decide what happens to someone's account.</summary>
[TestFixture]
[Category("Unit")]
public class ModerationActionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static ModerationAction Create(
        ModerationActionKind kind, DateTimeOffset? expiresAt = null, ReportReason reason = ReportReason.Spam) =>
        ModerationAction.Create(new CreateModerationActionParams
        {
            TargetUserId = "user_target",
            ActorUserId = "user_staff",
            Kind = kind,
            Reason = reason,
            PublicNote = "note",
            ExpiresAt = expiresAt,
        }, Now);

    // ── Creation ─────────────────────────────────────────────────────────────

    /// <summary>A suspension with no end date is a ban wearing a friendlier word, and the
    /// difference matters to the person it lands on. Refused rather than defaulted to some
    /// arbitrary duration nobody chose.</summary>
    [Test]
    public void A_suspension_without_an_expiry_is_refused()
    {
        Assert.That(() => Create(ModerationActionKind.Suspension),
            Throws.ArgumentException.With.Message.Contains("expiry"));
    }

    [TestCase(ModerationActionKind.Unban)]
    [TestCase(ModerationActionKind.Note)]
    public void An_action_that_cannot_expire_refuses_an_expiry(ModerationActionKind kind)
    {
        Assert.That(() => Create(kind, Now.AddDays(1)), Throws.ArgumentException);
    }

    [Test]
    public void A_ban_may_be_permanent()
    {
        var action = Create(ModerationActionKind.Ban);

        Assert.Multiple(() =>
        {
            Assert.That(action.ExpiresAt, Is.Null);
            Assert.That(action.IsActiveAt(Now.AddYears(50)), Is.True);
        });
    }

    /// <summary>Every action mints a reference, because that is what a banned account - which
    /// cannot sign in - types into the appeal form.</summary>
    [Test]
    public void Every_action_gets_a_public_reference()
    {
        var first = Create(ModerationActionKind.Ban);
        var second = Create(ModerationActionKind.Ban);

        Assert.Multiple(() =>
        {
            Assert.That(first.Reference, Does.StartWith(PublicReference.Prefix));
            Assert.That(first.Reference, Has.Length.EqualTo(PublicReference.TotalLength));
            Assert.That(second.Reference, Is.Not.EqualTo(first.Reference));
        });
    }

    // ── In force, or not ─────────────────────────────────────────────────────

    /// <summary>A warning is a permanent part of the record and restricts nothing.</summary>
    [TestCase(ModerationActionKind.Warning)]
    [TestCase(ModerationActionKind.Note)]
    [TestCase(ModerationActionKind.Unban)]
    public void Only_restricting_actions_are_ever_in_force(ModerationActionKind kind)
    {
        Assert.That(Create(kind).IsActiveAt(Now), Is.False);
    }

    [Test]
    public void A_suspension_stops_being_in_force_when_it_expires()
    {
        var action = Create(ModerationActionKind.Suspension, Now.AddHours(24));

        Assert.Multiple(() =>
        {
            Assert.That(action.IsActiveAt(Now.AddHours(23)), Is.True, "still running");
            Assert.That(action.IsActiveAt(Now.AddHours(24)), Is.False, "expiry is exclusive");
            Assert.That(action.IsActiveAt(Now.AddHours(25)), Is.False, "over");
            Assert.That(action.HasExpiredAt(Now.AddHours(25)), Is.True);
        });
    }

    // ── Revocation ───────────────────────────────────────────────────────────

    [Test]
    public void Revoking_a_ban_ends_it()
    {
        var action = Create(ModerationActionKind.Ban);

        Assert.Multiple(() =>
        {
            Assert.That(action.Revoke("user_staff2", "mistake", Now.AddHours(1)), Is.True);
            Assert.That(action.IsActiveAt(Now.AddHours(2)), Is.False);
            Assert.That(action.RevokedByUserId, Is.EqualTo("user_staff2"));
            Assert.That(action.RevocationReason, Is.EqualTo("mistake"));
        });
    }

    /// <summary>Two moderators clicking "lift" on the same row is a race, not an error - the second
    /// one should see the state the first produced rather than an exception.</summary>
    [Test]
    public void Revoking_twice_is_a_no_op_rather_than_an_error()
    {
        var action = Create(ModerationActionKind.Ban);
        action.Revoke("user_staff2", "first", Now.AddHours(1));

        Assert.Multiple(() =>
        {
            Assert.That(action.Revoke("user_staff3", "second", Now.AddHours(2)), Is.False);
            Assert.That(action.RevokedByUserId, Is.EqualTo("user_staff2"), "the first revocation stands");
            Assert.That(action.RevocationReason, Is.EqualTo("first"));
        });
    }

    [Test]
    public void A_warning_cannot_be_revoked()
    {
        Assert.That(Create(ModerationActionKind.Warning).Revoke("user_staff2", null, Now), Is.False);
    }

    // ── Appealable ───────────────────────────────────────────────────────────

    /// <summary>Notes are invisible to the account, and an already-lifted or expired sanction has
    /// nothing left to argue about. Offering an appeal on either sends someone to a form with
    /// nothing to say.</summary>
    [Test]
    public void Only_a_live_restriction_can_be_appealed()
    {
        var ban = Create(ModerationActionKind.Ban);
        var note = Create(ModerationActionKind.Note);
        var expired = Create(ModerationActionKind.Suspension, Now.AddHours(1));
        var lifted = Create(ModerationActionKind.Ban);
        lifted.Revoke("user_staff2", null, Now);

        Assert.Multiple(() =>
        {
            Assert.That(ban.IsAppealableAt(Now), Is.True);
            Assert.That(note.IsAppealableAt(Now), Is.False);
            Assert.That(expired.IsAppealableAt(Now.AddHours(2)), Is.False);
            Assert.That(lifted.IsAppealableAt(Now), Is.False);
        });
    }

    /// <summary>Notes and public messages are capped rather than rejected: a moderator who pasted a
    /// chat log into the wrong box should not lose the ban they were issuing.</summary>
    [Test]
    public void Overlong_notes_are_truncated_rather_than_rejected()
    {
        var action = ModerationAction.Create(new CreateModerationActionParams
        {
            TargetUserId = "user_target",
            ActorUserId = "user_staff",
            Kind = ModerationActionKind.Ban,
            Reason = ReportReason.Spam,
            PublicNote = new string('x', ModerationAction.MaxNoteLength + 500),
        }, Now);

        Assert.That(action.PublicNote, Has.Length.EqualTo(ModerationAction.MaxNoteLength));
    }
}

[TestFixture]
[Category("Unit")]
public class ModerationReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static ModerationReport Create(
        ReportReason reason = ReportReason.Spam,
        ReportSubjectKind kind = ReportSubjectKind.User,
        string? subjectId = null) =>
        ModerationReport.Create(new CreateReportParams
        {
            ReporterUserId = "user_reporter",
            TargetUserId = "user_target",
            SubjectKind = kind,
            SubjectId = subjectId,
            Reason = reason,
            Details = "they did a thing",
        }, Now);

    /// <summary>The physical-safety categories, and only those.</summary>
    [TestCase(ReportReason.ChildSafety)]
    [TestCase(ReportReason.SelfHarm)]
    [TestCase(ReportReason.ViolentThreats)]
    public void Physical_safety_reasons_are_critical(ReportReason reason)
    {
        Assert.That(ModerationReport.PriorityFor(reason), Is.EqualTo(ReportPriority.Critical));
    }

    [TestCase(ReportReason.Spam, ReportPriority.Normal)]
    [TestCase(ReportReason.Harassment, ReportPriority.High)]
    [TestCase(ReportReason.Other, ReportPriority.Low)]
    public void Other_reasons_land_below_critical(ReportReason reason, ReportPriority expected)
    {
        Assert.That(ModerationReport.PriorityFor(reason), Is.EqualTo(expected));
    }

    /// <summary>A subject id on a User report is a value nothing reads and everything has to decide
    /// whether to trust. Dropped once, at creation, rather than at each reader.</summary>
    [Test]
    public void A_user_report_drops_any_subject_id()
    {
        Assert.That(Create(subjectId: "mesg_whatever").SubjectId, Is.Null);
    }

    [Test]
    public void A_message_report_keeps_its_subject_id()
    {
        var report = Create(kind: ReportSubjectKind.Message, subjectId: "mesg_abc");
        Assert.That(report.SubjectId, Is.EqualTo("mesg_abc"));
    }

    /// <summary>Claiming a report is what takes it out of the unclaimed pile.</summary>
    [Test]
    public void Assigning_an_open_report_moves_it_to_triaged()
    {
        var report = Create();
        report.Assign("user_staff", Now);

        Assert.Multiple(() =>
        {
            Assert.That(report.Status, Is.EqualTo(ReportStatus.Triaged));
            Assert.That(report.AssignedToUserId, Is.EqualTo("user_staff"));
            Assert.That(report.IsOpen, Is.True);
        });
    }

    [Test]
    public void Releasing_a_report_leaves_it_open()
    {
        var report = Create();
        report.Assign("user_staff", Now);
        report.Assign(null, Now);

        Assert.Multiple(() =>
        {
            Assert.That(report.AssignedToUserId, Is.Null);
            Assert.That(report.IsOpen, Is.True);
        });
    }

    [Test]
    public void Resolving_records_who_closed_it_and_why()
    {
        var report = Create();
        report.Resolve(ReportStatus.Dismissed, "within the rules", "user_staff", Now.AddHours(1));

        Assert.Multiple(() =>
        {
            Assert.That(report.Status, Is.EqualTo(ReportStatus.Dismissed));
            Assert.That(report.IsOpen, Is.False);
            Assert.That(report.Resolution, Is.EqualTo("within the rules"));
            Assert.That(report.ResolvedByUserId, Is.EqualTo("user_staff"));
            Assert.That(report.ResolvedAt, Is.EqualTo(Now.AddHours(1)));
        });
    }

    /// <summary>Reopening clears the resolution but keeps the assignee - whoever closed it wrongly
    /// is usually the person who should look again.</summary>
    [Test]
    public void Reopening_clears_the_resolution_and_keeps_the_assignee()
    {
        var report = Create();
        report.Assign("user_staff", Now);
        report.Resolve(ReportStatus.Dismissed, "closed", "user_staff", Now);
        report.Reopen(Now.AddHours(1));

        Assert.Multiple(() =>
        {
            Assert.That(report.Status, Is.EqualTo(ReportStatus.Triaged));
            Assert.That(report.Resolution, Is.Null);
            Assert.That(report.ResolvedAt, Is.Null);
            Assert.That(report.AssignedToUserId, Is.EqualTo("user_staff"));
        });
    }

    [Test]
    public void Overlong_details_are_truncated_rather_than_rejected()
    {
        var report = ModerationReport.Create(new CreateReportParams
        {
            TargetUserId = "user_target",
            Reason = ReportReason.Spam,
            Details = new string('x', ModerationReport.MaxDetailsLength + 1000),
        }, Now);

        Assert.That(report.Details, Has.Length.EqualTo(ModerationReport.MaxDetailsLength));
    }
}

[TestFixture]
[Category("Unit")]
public class SupportTicketTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static (SupportTicket Ticket, string Token) Create() =>
        SupportTicket.Create(new CreateSupportTicketParams
        {
            ContactEmail = "  Person@Example.COM ",
            Subject = "help",
            Category = SupportTicketCategory.Account,
        }, Now);

    /// <summary>Lower-cased on the way in, so "Person@Example.com" and "person@example.com" are the
    /// same requester at lookup time.</summary>
    [Test]
    public void The_contact_address_is_normalised()
    {
        Assert.That(Create().Ticket.ContactEmail, Is.EqualTo("person@example.com"));
    }

    /// <summary>The token is returned once and stored only as a hash.</summary>
    [Test]
    public void The_access_token_is_stored_only_as_a_hash()
    {
        var (ticket, token) = Create();

        Assert.Multiple(() =>
        {
            Assert.That(token, Is.Not.Empty);
            Assert.That(ticket.AccessTokenHash, Has.Length.EqualTo(32), "SHA-256");
            Assert.That(System.Text.Encoding.UTF8.GetString(ticket.AccessTokenHash), Does.Not.Contain(token));
            Assert.That(ticket.TokenMatches(token), Is.True);
        });
    }

    [TestCase("")]
    [TestCase(null)]
    [TestCase("not-the-token")]
    public void A_wrong_token_does_not_match(string? token)
    {
        Assert.That(Create().Ticket.TokenMatches(token), Is.False);
    }

    /// <summary>Two tickets never share a token - the same draw twice would mean one requester
    /// could open another's ticket.</summary>
    [Test]
    public void Tokens_and_references_are_unique_per_ticket()
    {
        var (first, firstToken) = Create();
        var (second, secondToken) = Create();

        Assert.Multiple(() =>
        {
            Assert.That(secondToken, Is.Not.EqualTo(firstToken));
            Assert.That(second.Reference, Is.Not.EqualTo(first.Reference));
            Assert.That(second.TokenMatches(firstToken), Is.False);
        });
    }

    /// <summary>Appending moves the ticket to whichever side now owes a reply.</summary>
    [Test]
    public void A_requester_reply_puts_the_ticket_back_on_staff()
    {
        var (ticket, _) = Create();
        ticket.Append(SupportMessageAuthorKind.Staff, "user_staff", "here you go", false, Now);
        Assert.That(ticket.Status, Is.EqualTo(SupportTicketStatus.AwaitingRequester));

        ticket.Append(SupportMessageAuthorKind.Requester, null, "still broken", false, Now.AddHours(1));
        Assert.That(ticket.Status, Is.EqualTo(SupportTicketStatus.AwaitingStaff));
    }

    /// <summary>Swallowing "this is still broken" because a box was ticked is the worst thing a
    /// support queue does.</summary>
    [Test]
    public void Replying_to_a_resolved_ticket_reopens_it()
    {
        var (ticket, _) = Create();
        ticket.SetStatus(SupportTicketStatus.Resolved, Now);

        ticket.Append(SupportMessageAuthorKind.Requester, null, "still broken", false, Now.AddDays(1));

        Assert.That(ticket.Status, Is.EqualTo(SupportTicketStatus.AwaitingStaff));
    }

    /// <summary>An internal note is staff talking to staff.</summary>
    [Test]
    public void An_internal_note_changes_no_status_and_no_activity_time()
    {
        var (ticket, _) = Create();
        var before = ticket.LastActivityAt;

        var message = ticket.Append(SupportMessageAuthorKind.Staff, "user_staff", "looks like abuse", true, Now.AddHours(2));

        Assert.Multiple(() =>
        {
            Assert.That(message.IsInternal, Is.True);
            Assert.That(ticket.Status, Is.EqualTo(SupportTicketStatus.Open));
            Assert.That(ticket.LastActivityAt, Is.EqualTo(before));
        });
    }

    /// <summary>A requester-authored "internal" note would be a message hidden from the only other
    /// participant, which is not a thing this system has.</summary>
    [Test]
    public void Only_staff_can_leave_an_internal_note()
    {
        var (ticket, _) = Create();
        var message = ticket.Append(SupportMessageAuthorKind.Requester, null, "psst", true, Now);

        Assert.That(message.IsInternal, Is.False);
    }
}

[TestFixture]
[Category("Unit")]
public class ModerationAppealTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static ModerationAppeal Create() =>
        ModerationAppeal.Create(new CreateAppealParams
        {
            ActionId = "mact_abc",
            ContactEmail = " Person@Example.COM ",
            Body = "you got it wrong",
        }, Now);

    [Test]
    public void The_contact_address_is_normalised_so_the_status_lookup_can_match_it()
    {
        Assert.That(Create().ContactEmail, Is.EqualTo("person@example.com"));
    }

    [Test]
    public void A_new_appeal_is_open_and_undecided()
    {
        var appeal = Create();

        Assert.Multiple(() =>
        {
            Assert.That(appeal.Status, Is.EqualTo(AppealStatus.Pending));
            Assert.That(appeal.IsOpen, Is.True);
            Assert.That(appeal.DecidedAt, Is.Null);
            Assert.That(appeal.Reference, Does.StartWith(PublicReference.Prefix));
        });
    }

    [TestCase(true, AppealStatus.Granted)]
    [TestCase(false, AppealStatus.Denied)]
    public void Deciding_records_the_outcome_and_the_note(bool granted, AppealStatus expected)
    {
        var appeal = Create();
        appeal.Decide(granted, "  we looked again  ", "user_staff", Now.AddDays(1));

        Assert.Multiple(() =>
        {
            Assert.That(appeal.Status, Is.EqualTo(expected));
            Assert.That(appeal.IsOpen, Is.False);
            Assert.That(appeal.DecisionNote, Is.EqualTo("we looked again"));
            Assert.That(appeal.DecidedByUserId, Is.EqualTo("user_staff"));
            Assert.That(appeal.DecidedAt, Is.EqualTo(Now.AddDays(1)));
        });
    }

    /// <summary>Claiming only moves a Pending appeal.</summary>
    [Test]
    public void Claiming_a_decided_appeal_does_nothing()
    {
        var appeal = Create();
        appeal.Decide(false, "no", "user_staff", Now);
        appeal.Claim("user_staff2", Now.AddHours(1));

        Assert.Multiple(() =>
        {
            Assert.That(appeal.Status, Is.EqualTo(AppealStatus.Denied));
            Assert.That(appeal.DecidedByUserId, Is.EqualTo("user_staff"));
        });
    }
}

[TestFixture]
[Category("Unit")]
public class PublicReferenceTests
{
    [Test]
    public void A_new_reference_has_the_prefix_and_a_fixed_length()
    {
        var reference = PublicReference.New();

        Assert.Multiple(() =>
        {
            Assert.That(reference, Does.StartWith(PublicReference.Prefix));
            Assert.That(reference, Has.Length.EqualTo(PublicReference.TotalLength));
        });
    }

    /// <summary>Not a guarantee of uniqueness - 40 bits is not a credential and is not treated as
    /// one - but a generator that repeats itself over a few thousand draws is broken.</summary>
    [Test]
    public void References_do_not_repeat_over_a_realistic_run()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 5000; i++) seen.Add(PublicReference.New());

        Assert.That(seen, Has.Count.EqualTo(5000));
    }

    /// <summary>A reference is read off a screenshot or out of an email far more often than it is
    /// copied, so refusing a correctly-remembered code because of a space would be refusing a right
    /// answer.</summary>
    [TestCase("VNT-4KP2R9XQ")]
    [TestCase("vnt-4kp2r9xq")]
    [TestCase("VNT4KP2R9XQ")]
    [TestCase("  vnt 4kp2 r9xq  ")]
    [TestCase("4KP2R9XQ")]
    [TestCase("4kp2r9xq")]
    [TestCase("4KP2-R9XQ")]
    public void Normalise_accepts_what_a_person_would_actually_type(string input)
    {
        Assert.That(PublicReference.Normalise(input), Is.EqualTo("VNT-4KP2R9XQ"));
    }

    /// <summary>A bare body that happens to start with the prefix letters is data, not a prefix.
    /// Deciding on the total length rather than by looking for "VNT" is what makes this work.</summary>
    [Test]
    public void A_body_starting_with_the_prefix_letters_is_not_mistaken_for_one()
    {
        Assert.That(PublicReference.Normalise("VNTP2R9X"), Is.EqualTo("VNT-VNTP2R9X"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("4KP2R9X")]              // one short
    [TestCase("4KP2R9XQZ")]            // one long
    [TestCase("VNT-4KP2R9XQZZ")]       // long with a prefix
    [TestCase("4KP2R9XI")]             // I is not in the alphabet
    [TestCase("4KP2R9XL")]             // nor is L
    [TestCase("4KP2R9XU")]             // nor is U
    [TestCase("4KP2R9X!")]
    [TestCase("this is not a reference at all")]
    public void Normalise_refuses_anything_that_is_not_a_reference(string? input)
    {
        Assert.That(PublicReference.Normalise(input), Is.Null);
    }

    /// <summary>Round-trip: anything the generator produces must survive being read back.</summary>
    [Test]
    public void Every_generated_reference_normalises_back_to_itself()
    {
        for (var i = 0; i < 500; i++)
        {
            var reference = PublicReference.New();

            Assert.That(PublicReference.Normalise(reference), Is.EqualTo(reference));
            Assert.That(PublicReference.Normalise(reference.ToLowerInvariant()), Is.EqualTo(reference));
        }
    }
}
