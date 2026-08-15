using System.Text.Json;
using Billing.Application.Dtos;
using Billing.Application.Services;
using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Echo.Entitlements.Model;

namespace Billing.Tests;

/// <summary>
/// The small closed vocabularies the checkout surface is built out of: the subject-kind casing, the
/// Stripe status strings, the plan subject-kind inference, and the idempotency keys.
/// </summary>
[TestFixture]
public class CheckoutVocabularyTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    // ── The subject-kind converter ───────────────────────────────────────────

    [Test]
    public void Subject_kinds_are_written_in_lower_case()
    {
        Assert.Multiple(() =>
        {
            Assert.That(JsonSerializer.Serialize(
                new CreateSubscriptionRequest("pro", SubjectKind.Guild, "gld_1"), Wire),
                Does.Contain("\"subjectKind\":\"guild\""));

            Assert.That(JsonSerializer.Serialize(
                new CreateSubscriptionRequest("venta_plus", SubjectKind.User, "user_1"), Wire),
                Does.Contain("\"subjectKind\":\"user\""));
        });
    }

    /// <summary>The contract's own request example spells it <c>"Guild"</c> while everything this
    /// surface writes is lowercase, so a client echoing back what it was sent - in either
    /// direction - has to be accepted.</summary>
    [TestCase("\"Guild\"", SubjectKind.Guild)]
    [TestCase("\"guild\"", SubjectKind.Guild)]
    [TestCase("\"USER\"", SubjectKind.User)]
    [TestCase("\"user\"", SubjectKind.User)]
    public void Subject_kinds_are_read_in_either_casing(string json, SubjectKind expected)
    {
        var request = JsonSerializer.Deserialize<CreateSubscriptionRequest>(
            $"{{\"planName\":\"pro\",\"subjectKind\":{json},\"subjectId\":\"gld_1\"}}", Wire);

        Assert.That(request!.SubjectKind, Is.EqualTo(expected));
    }

    [Test]
    public void A_subject_kind_that_is_neither_is_refused_rather_than_defaulted()
    {
        Assert.That(() => JsonSerializer.Deserialize<CreateSubscriptionRequest>(
                "{\"planName\":\"pro\",\"subjectKind\":\"channel\",\"subjectId\":\"chn_1\"}", Wire),
            Throws.InstanceOf<JsonException>());
    }

    // ── The status vocabulary ────────────────────────────────────────────────

    /// <summary>The two halves of the status translation have to stay inverses.</summary>
    [Test]
    public void Every_status_round_trips_through_the_wire_spelling()
    {
        Assert.Multiple(() =>
        {
            foreach (var status in Enum.GetValues<SubscriptionStatus>())
            {
                if (status == SubscriptionStatus.Unknown) continue;

                var wire = SubscriptionStatusWire.Of(status);

                Assert.That(SubscriptionStatuses.Parse(wire), Is.EqualTo(status),
                    $"'{wire}' is what {status} is written as and it does not parse back");
            }
        });
    }

    [Test]
    public void The_wire_spellings_are_Stripes_own_snake_case()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SubscriptionStatusWire.Of(SubscriptionStatus.PastDue), Is.EqualTo("past_due"));
            Assert.That(SubscriptionStatusWire.Of(SubscriptionStatus.IncompleteExpired),
                Is.EqualTo("incomplete_expired"));
            Assert.That(SubscriptionStatusWire.Of(SubscriptionStatus.Canceled), Is.EqualTo("canceled"));
        });
    }

    /// <summary>A status Stripe adds must not take the screen down.</summary>
    [Test]
    public void An_unknown_status_is_a_word_rather_than_an_exception()
    {
        Assert.That(SubscriptionStatusWire.Of(SubscriptionStatus.Unknown), Is.EqualTo("unknown"));
    }

    // ── Whose plan a plan is ─────────────────────────────────────────────────

    [Test]
    public void A_plan_that_sets_a_guild_key_is_a_guild_plan()
    {
        var kind = PlanSubjectKinds.Of(
            "pro", ["voice.max_participants", "voice.video_ceiling"], "free_user");

        Assert.That(kind, Is.EqualTo(SubjectKind.Guild));
    }

    [Test]
    public void A_plan_that_sets_only_user_and_paired_keys_is_a_user_plan()
    {
        var kind = PlanSubjectKinds.Of(
            "venta_plus", ["voice.video_ceiling", "user.max_devices"], "free_user");

        Assert.That(kind, Is.EqualTo(SubjectKind.User));
    }

    /// <summary>The tie-break.</summary>
    [Test]
    public void A_plan_of_paired_keys_only_falls_to_the_configured_default_user_plan()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                PlanSubjectKinds.Of("free_user", ["voice.video_ceiling"], "free_user"),
                Is.EqualTo(SubjectKind.User));

            Assert.That(
                PlanSubjectKinds.Of("mystery", ["voice.video_ceiling"], "free_user"),
                Is.EqualTo(SubjectKind.Guild));
        });
    }

    [Test]
    public void A_key_the_catalogue_has_never_heard_of_does_not_decide_anything()
    {
        var kind = PlanSubjectKinds.Of("odd", ["not.a.key", "user.max_devices"], null);

        Assert.That(kind, Is.EqualTo(SubjectKind.User));
    }

    // ── The idempotency keys ─────────────────────────────────────────────────

    /// <summary>Asserted literally, for the same reason the product and price keys are.</summary>
    [Test]
    public void The_customer_and_subscription_keys_are_exactly_the_documented_strings()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StripeIdempotencyKey.ForCustomer("user_42").Value,
                Is.EqualTo("venta:customer:user_42"));

            Assert.That(StripeIdempotencyKey.ForSubscription("guild", "gld_7", "price_abc").Value,
                Is.EqualTo("venta:subscription:guild:gld_7:price_abc"));
        });
    }

    /// <summary>
    /// A trial's key is its own, and it is keyed on the campaign rather than on the price.
    /// </summary>
    [Test]
    public void The_trial_key_is_its_own_and_names_the_campaign()
    {
        var trial = StripeIdempotencyKey.ForTrial("guild", "gld_7", "pro-trial-2026");

        Assert.Multiple(() =>
        {
            Assert.That(trial.Value, Is.EqualTo("venta:trial:guild:gld_7:pro-trial-2026"));

            Assert.That(trial.Value,
                Is.Not.EqualTo(StripeIdempotencyKey.ForSubscription("guild", "gld_7", "price_abc").Value));

            Assert.That(
                StripeIdempotencyKey.ForTrial("guild", "gld_7", "pro-trial-2026", automaticTax: false).Value,
                Is.EqualTo("venta:trial:guild:gld_7:pro-trial-2026:untaxed"));

            Assert.That(() => StripeIdempotencyKey.ForTrial("guild", "gld_7", " "),
                Throws.InstanceOf<ArgumentException>());
        });
    }

    /// <summary>The no-automatic-tax retry is a different request, so it must present a different key
    /// or Stripe refuses the replay for mismatched parameters.</summary>
    [Test]
    public void The_untaxed_fallback_key_differs_from_the_taxed_one()
    {
        var taxed = StripeIdempotencyKey.ForSubscription("guild", "gld_7", "price_abc");
        var untaxed = StripeIdempotencyKey.ForSubscription("guild", "gld_7", "price_abc", automaticTax: false);

        Assert.Multiple(() =>
        {
            Assert.That(untaxed.Value, Is.EqualTo("venta:subscription:guild:gld_7:price_abc:untaxed"));
            Assert.That(untaxed.Value, Is.Not.EqualTo(taxed.Value));
        });
    }

    /// <summary>The update keys are fresh per call, and that is the point.</summary>
    [Test]
    public void A_request_key_is_never_the_same_key_twice()
    {
        var first = StripeIdempotencyKey.ForRequest("cancel", "sub_1");
        var second = StripeIdempotencyKey.ForRequest("cancel", "sub_1");

        Assert.Multiple(() =>
        {
            Assert.That(first.Value, Does.StartWith("venta:cancel:sub_1:"));
            Assert.That(second.Value, Does.StartWith("venta:cancel:sub_1:"));
            Assert.That(first.Value, Is.Not.EqualTo(second.Value));
        });
    }

    [Test]
    public void None_of_the_new_keys_can_be_built_from_nothing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => StripeIdempotencyKey.ForCustomer(" "),
                Throws.InstanceOf<ArgumentException>());

            Assert.That(() => StripeIdempotencyKey.ForSubscription("guild", "gld_1", ""),
                Throws.InstanceOf<ArgumentException>());

            Assert.That(() => StripeIdempotencyKey.ForRequest("cancel", ""),
                Throws.InstanceOf<ArgumentException>());
        });
    }
}
