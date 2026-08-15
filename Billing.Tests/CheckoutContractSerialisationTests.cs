using System.Text.Json;
using System.Text.Json.Serialization;
using Billing.Application.Dtos;
using Billing.Domain.Aggregates;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Wire;

namespace Billing.Tests;

/// <summary>The exact bytes the checkout surface puts on the wire.</summary>
[TestFixture]
public class CheckoutContractSerialisationTests
{
    /// <summary>Program.cs, restated: web defaults plus the string enum converter.</summary>
    private static readonly JsonSerializerOptions Wire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string Json(object value) => JsonSerializer.Serialize(value, Wire);

    private static CataloguePlanDto ProPlan() => new(
        "pro",
        "Pro",
        "For communities that stream.",
        3,
        SubjectKind.Guild,
        2900,
        "usd",
        "month",
        true,
        new Dictionary<string, EntitlementValueDto>(StringComparer.Ordinal)
        {
            ["voice.max_participants"] =
                EntitlementValueDto.From(EntitlementKeys.VoiceMaxParticipants, EntitlementValue.OfNumber(75)),
            ["voice.video_ceiling"] =
                EntitlementValueDto.From(EntitlementKeys.VoiceVideoCeiling, EntitlementValue.OfRank(6)),
            ["guild.bots_installed"] = EntitlementValueDto.From(
                EntitlementKeys.GuildBotsInstalled,
                EntitlementValue.OfNumber(EntitlementValue.Unlimited)),
            ["guild.vanity_url"] =
                EntitlementValueDto.From(EntitlementKeys.GuildVanityUrl, EntitlementValue.OfFlag(true)),
        });

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public void A_catalogue_plan_serialises_exactly_as_the_contract_documents_it()
    {
        var json = Json(ProPlan());

        Assert.That(json, Is.EqualTo(
            "{\"name\":\"pro\","
            + "\"displayName\":\"Pro\","
            + "\"description\":\"For communities that stream.\","
            + "\"versionNumber\":3,"
            + "\"subjectKind\":\"guild\","
            + "\"priceMinorUnits\":2900,"
            + "\"currency\":\"usd\","
            + "\"interval\":\"month\","
            + "\"purchasable\":true,"
            + "\"entitlements\":{"
            + "\"voice.max_participants\":{\"kind\":\"numeric\",\"value\":75,\"unlimited\":false},"
            + "\"voice.video_ceiling\":{\"kind\":\"ladder\",\"rung\":\"2160p60\",\"rank\":6,"
            + "\"ladder\":\"video_quality\"},"
            + "\"guild.bots_installed\":{\"kind\":\"numeric\",\"value\":null,\"unlimited\":true},"
            + "\"guild.vanity_url\":{\"kind\":\"flag\",\"granted\":true}}}"));
    }

    [Test]
    public void A_subscription_serialises_exactly_as_the_contract_documents_it()
    {
        var subscription = new Subscription
        {
            Id = "subs_01",
            StripeSubscriptionId = "sub_1QabcTest",
            PayerUserId = "user_payer",
            SubjectKind = SubjectKind.Guild,
            SubjectId = "gld_test",
            PlanId = "plan_pro",
            VersionNumber = 3,
            Status = SubscriptionStatus.Active,
            CurrentPeriodEnd = new DateTimeOffset(2026, 9, 14, 10, 12, 0, TimeSpan.Zero),
            CancelAtPeriodEnd = false,
        };

        var plan = new Plan
        {
            Id = "plan_pro", Name = "pro", DisplayName = "Pro", CurrentVersionNumber = 3,
            CreatedBy = "system",
        };

        var version = new PlanVersion
        {
            Id = "plnv_3", PlanId = plan.Id, VersionNumber = 3, ValuesJson = "{}",
            PriceMinorUnits = 2900, Currency = "usd", Reason = "Launch.", CreatedBy = "system",
        };

        var json = Json(SubscriptionDto.From(subscription, plan, version, isPayer: true));

        Assert.That(json, Is.EqualTo(
            "{\"id\":\"sub_1QabcTest\","
            + "\"subjectKind\":\"guild\","
            + "\"subjectId\":\"gld_test\","
            + "\"planName\":\"pro\","
            + "\"planDisplayName\":\"Pro\","
            + "\"versionNumber\":3,"
            + "\"status\":\"active\","
            + "\"currentPeriodEnd\":\"2026-09-14T10:12:00+00:00\","
            + "\"cancelAtPeriodEnd\":false,"
            + "\"gracePeriodEndsAt\":null,"
            + "\"priceMinorUnits\":2900,"
            + "\"currency\":\"usd\","
            + "\"interval\":\"month\","
            + "\"isPayer\":true}"));
    }

    [Test]
    public void The_catalogue_envelope_carries_the_ladders_once()
    {
        var envelope = new CatalogueDto(
            true,
            "usd",
            [ProPlan()],
            new Dictionary<string, IReadOnlyList<EntitlementRungDto>>(StringComparer.Ordinal)
            {
                ["video_quality"] = [new EntitlementRungDto("none", 0, 0, 0)],
            });

        var json = Json(envelope);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.StartWith("{\"enabled\":true,\"currency\":\"usd\",\"plans\":["));
            Assert.That(json, Does.EndWith(
                "\"ladders\":{\"video_quality\":[{\"rung\":\"none\",\"rank\":0,\"maxHeight\":0,"
                + "\"maxFramerate\":0}]}}"));

            // Once on the envelope and never per plan. The client reads rung metrics from one place.
            Assert.That(json.Split("\"ladders\"", StringSplitOptions.None), Has.Length.EqualTo(2));
        });
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The one number that must never be a number.</b> <c>EntitlementValue.Unlimited</c> is
    /// <c>long.MaxValue</c>, which is larger than JavaScript's <c>Number.MAX_SAFE_INTEGER</c>: a JS
    /// client parsing it gets a different value back, every comparison against a known sentinel
    /// fails, and there is no client-side symptom until something quietly misbehaves.
    /// </summary>
    [Test]
    public void An_unlimited_entitlement_is_null_and_a_flag_rather_than_a_huge_number()
    {
        var json = Json(ProPlan());

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"value\":null,\"unlimited\":true"));
            Assert.That(json, Does.Not.Contain(EntitlementValue.Unlimited.ToString()));
        });
    }

    [Test]
    public void A_free_plan_is_published_with_a_null_price_and_purchasable_false()
    {
        var free = new CataloguePlanDto(
            "free", "Free", null, 1, SubjectKind.Guild, null, null, "month", false,
            new Dictionary<string, EntitlementValueDto>(StringComparer.Ordinal));

        var json = Json(free);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"priceMinorUnits\":null"));
            Assert.That(json, Does.Contain("\"currency\":null"));
            Assert.That(json, Does.Contain("\"purchasable\":false"));
        });
    }

    [Test]
    public void A_user_plan_says_user_in_lower_case()
    {
        var plus = new CataloguePlanDto(
            "venta_plus", "Venta Plus", null, 1, SubjectKind.User, 600, "usd", "month", true,
            new Dictionary<string, EntitlementValueDto>(StringComparer.Ordinal));

        Assert.That(Json(plus), Does.Contain("\"subjectKind\":\"user\""));
    }

    /// <summary>The nullability decision on <c>interval</c>, written out as bytes.</summary>
    [Test]
    public void A_subscription_whose_plan_version_is_gone_writes_a_null_interval()
    {
        var subscription = new Subscription
        {
            Id = "subs_02",
            StripeSubscriptionId = "sub_orphan",
            PayerUserId = "user_payer",
            SubjectKind = SubjectKind.Guild,
            SubjectId = "gld_test",
            PlanId = "plan_pro",
            VersionNumber = 7,
            Status = SubscriptionStatus.Active,
        };

        var plan = new Plan
        {
            Id = "plan_pro", Name = "pro", DisplayName = "Pro", CurrentVersionNumber = 3,
            CreatedBy = "system",
        };

        var json = Json(SubscriptionDto.From(subscription, plan, version: null, isPayer: true));

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"priceMinorUnits\":null"));
            Assert.That(json, Does.Contain("\"currency\":null"));
            Assert.That(json, Does.Contain("\"interval\":null"));
        });
    }

    /// <summary>A create response with nothing to confirm.</summary>
    [Test]
    public void A_null_client_secret_is_written_rather_than_omitted()
    {
        var response = new CreateSubscriptionResponse(
            new SubscriptionDto("sub_1", SubjectKind.User, "user_1", "venta_plus", "Venta Plus", 1,
                "active", null, false, null, 600, "usd", "month", true),
            ClientSecret: null);

        Assert.That(Json(response), Does.Contain("\"clientSecret\":null"));
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    /// <summary>The casing is the whole point of the explicit converter: the same client screen
    /// renders this and the entitlement snapshot, and two spellings of one concept on one screen is
    /// somebody writing <c>===</c> and being wrong.</summary>
    [Test]
    public void No_customer_facing_payload_ever_says_Guild_with_a_capital_G()
    {
        var payloads = new[]
        {
            Json(ProPlan()),
            Json(new SubscriptionDto("sub_1", SubjectKind.Guild, "gld_1", "pro", "Pro", 1, "active",
                null, false, null, 2900, "usd", "month", false)),
            Json(new CreateSubscriptionRequest("pro", SubjectKind.Guild, "gld_1")),
        };

        Assert.Multiple(() =>
        {
            foreach (var payload in payloads)
            {
                Assert.That(payload, Does.Not.Contain("\"Guild\""), payload);
                Assert.That(payload, Does.Not.Contain("\"User\""), payload);
            }
        });
    }
}
