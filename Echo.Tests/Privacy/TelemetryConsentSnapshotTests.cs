using AppEnvironment;
using Echo.Tests.Support;
using Sentry;

namespace Echo.Tests.Privacy;

/// <summary>
/// The consent mirror Social, Guild and Messaging put behind
/// <c>SentryPrivacy.HasDataCollectionConsent</c> (T0-4).
///
/// <para>Every test here is really the same assertion from a different angle: the only way this
/// class may ever answer <c>true</c> is for an account it has recently and positively confirmed said
/// yes. Not-yet-known, no-longer-fresh, forgotten, over-capacity and null all have to answer
/// <c>false</c>, because a wrong "false" costs a pseudonymized stack trace and a wrong "true" puts
/// somebody's email address in a third-party error tracker.</para>
/// </summary>
[TestFixture]
public class TelemetryConsentSnapshotTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(45);

    private static TelemetryConsentSnapshot Snapshot(
        MutableClock clock, int maxTracked = 100, TimeSpan? idleEviction = null) =>
        new(clock, Lifetime, idleEviction ?? TimeSpan.FromMinutes(10), maxTracked);

    [Test]
    public void An_account_it_has_never_resolved_has_not_consented()
    {
        var snapshot = Snapshot(new MutableClock());

        Assert.That(snapshot.Has("user_1"), Is.False);
    }

    [Test]
    public void Asking_about_an_unknown_account_registers_it_for_the_next_refresh()
    {
        var snapshot = Snapshot(new MutableClock());

        snapshot.Has("user_1");

        Assert.That(snapshot.TakeRefreshSet(), Is.EquivalentTo(new[] { "user_1" }));
    }

    [Test]
    public void A_resolved_consent_is_answered_until_it_goes_stale()
    {
        var clock = new MutableClock();
        var snapshot = Snapshot(clock);

        snapshot.Set("user_1", true);
        Assert.That(snapshot.Has("user_1"), Is.True);

        clock.Advance(Lifetime - TimeSpan.FromSeconds(1));
        Assert.That(snapshot.Has("user_1"), Is.True);

        // Past its lifetime the entry stops being trusted. This is what stops a refresh loop that
        // has silently died from keeping a withdrawn consent alive forever.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.That(snapshot.Has("user_1"), Is.False);
    }

    [Test]
    public void A_withdrawal_takes_effect_on_the_next_refresh_not_on_a_cache_expiry()
    {
        var clock = new MutableClock();
        var snapshot = Snapshot(clock);

        snapshot.Set("user_1", true);
        Assert.That(snapshot.Has("user_1"), Is.True);

        // What the refresh loop does when the underlying privacy record now says false.
        snapshot.Set("user_1", false);

        Assert.That(snapshot.Has("user_1"), Is.False);
    }

    [Test]
    public void Forgetting_an_account_drops_it_back_to_no_consent()
    {
        var snapshot = Snapshot(new MutableClock());

        snapshot.Set("user_1", true);
        snapshot.Forget("user_1");

        Assert.That(snapshot.Has("user_1"), Is.False);
    }

    [Test]
    public void A_missing_or_empty_id_never_consents()
    {
        var snapshot = Snapshot(new MutableClock());

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Has(null), Is.False);
            Assert.That(snapshot.Has(string.Empty), Is.False);
            Assert.That(snapshot.TrackedCount, Is.Zero, "a null id must not occupy a tracking slot");
        });
    }

    [Test]
    public void Tracking_is_capped_so_a_burst_of_failing_requests_cannot_grow_it_without_limit()
    {
        var snapshot = Snapshot(new MutableClock(), maxTracked: 3);

        for (var i = 0; i < 50; i++) snapshot.Has($"user_{i}");

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.TrackedCount, Is.EqualTo(3));
            Assert.That(snapshot.Has("user_49"), Is.False, "an id that never got tracked still answers 'no consent'");
        });
    }

    [Test]
    public void Accounts_nothing_has_asked_about_in_a_while_stop_being_refreshed()
    {
        var clock = new MutableClock();
        var snapshot = Snapshot(clock, idleEviction: TimeSpan.FromMinutes(10));

        snapshot.Has("user_1");
        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.TakeRefreshSet(), Is.Empty);
            Assert.That(snapshot.TrackedCount, Is.Zero);
        });
    }

    /// <summary>
    /// The end of the wire: the snapshot installed as the gate actually changes what leaves the
    /// process. Asserted here rather than trusted, because the failure mode is silent - a delegate
    /// that is never wired looks exactly like one that always says "no", right up until somebody
    /// wires it wrong and it always says "yes".
    /// </summary>
    [Test]
    public void Behind_SentryPrivacy_a_non_consenting_account_is_pseudonymized_and_a_consenting_one_is_not()
    {
        var original = SentryPrivacy.HasDataCollectionConsent;
        try
        {
            var snapshot = Snapshot(new MutableClock());
            SentryPrivacy.HasDataCollectionConsent = snapshot.Has;

            snapshot.Set("consenting_user", true);

            var withheld = new SentryEvent();
            withheld.User.Id = "silent_user";
            withheld.User.Email = "someone@example.com";
            SentryPrivacy.Scrub(withheld);

            var permitted = new SentryEvent();
            permitted.User.Id = "consenting_user";
            permitted.User.Email = "someone@example.com";
            SentryPrivacy.Scrub(permitted);

            Assert.Multiple(() =>
            {
                Assert.That(withheld.User.Id, Is.EqualTo(SentryPrivacy.Pseudonymize("silent_user")));
                Assert.That(withheld.User.Email, Is.Null);
                Assert.That(permitted.User.Id, Is.EqualTo("consenting_user"));
                Assert.That(permitted.User.Email, Is.EqualTo("someone@example.com"));
            });
        }
        finally
        {
            SentryPrivacy.HasDataCollectionConsent = original;
        }
    }
}
