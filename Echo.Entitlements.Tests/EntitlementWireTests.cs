using System.Text.Json;
using System.Text.Json.Nodes;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Wire;

namespace Echo.Entitlements.Tests;

/// <summary>The client-facing contract, asserted rather than described.</summary>
[TestFixture]
public class EntitlementWireTests
{
    /// <summary>The largest integer JavaScript can hold exactly.</summary>
    private const long MaxSafeInteger = 9007199254740991;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly EntitlementSubject Guild = EntitlementSubject.ForGuild("guild-1");
    private static readonly EntitlementSubject Member = EntitlementSubject.ForUser("user-1");

    // ── Unlimited never crosses the wire as a number ──────────────────────────

    /// <summary>The headline guarantee.</summary>
    [Test]
    public void Unlimited_is_a_null_limit_and_a_flag_never_a_number()
    {
        var json = JsonSerializer.Serialize(EntitlementValueDto.Number(EntitlementValue.Unlimited), Web);

        Assert.Multiple(() =>
        {
            Assert.That(json, Is.EqualTo("""{"kind":"numeric","value":null,"unlimited":true}"""));
            Assert.That(json, Does.Not.Contain("9223372036854775807"),
                "the sentinel itself must never appear in the bytes");
        });
    }

    /// <summary>A real limit is a real number, and stays one.</summary>
    [Test]
    public void A_bounded_limit_carries_its_number()
    {
        var json = JsonSerializer.Serialize(EntitlementValueDto.Number(26214400), Web);

        Assert.That(json, Is.EqualTo("""{"kind":"numeric","value":26214400,"unlimited":false}"""));
    }

    /// <summary>
    /// The catalogue defaults are unlimited nearly everywhere, so a snapshot on an instance with no
    /// plan configured is the case most likely to ship the sentinel.
    /// </summary>
    [Test]
    public void No_number_in_a_default_snapshot_exceeds_what_javascript_can_hold()
    {
        var json = JsonSerializer.Serialize(Snapshot(Guild), Web);
        var numbers = JsonNode.Parse(json)!["entitlements"]!.AsObject()
            .Select(entry => entry.Value!["value"])
            .OfType<JsonValue>()
            .Select(value => value.GetValue<long>())
            .ToList();

        Assert.That(numbers, Is.All.LessThanOrEqualTo(MaxSafeInteger));
    }

    [Test]
    public void Flags_and_ladder_rungs_carry_only_their_own_fields()
    {
        Assert.Multiple(() =>
        {
            Assert.That(JsonSerializer.Serialize(EntitlementValueDto.Flag(true), Web),
                Is.EqualTo("""{"kind":"flag","granted":true}"""));
            Assert.That(
                JsonSerializer.Serialize(
                    EntitlementValueDto.OnLadder(EntitlementLadders.VideoQuality, 2), Web),
                Is.EqualTo("""{"kind":"ladder","rung":"720p30","rank":2,"ladder":"video_quality"}"""));
        });
    }

    [Test]
    public void A_value_survives_a_round_trip_including_unlimited()
    {
        var key = EntitlementKeys.StorageGuildQuotaBytes;
        var unlimited = EntitlementValueDto.From(key, EntitlementValue.OfNumber(EntitlementValue.Unlimited));

        var back = JsonSerializer.Deserialize<EntitlementValueDto>(
            JsonSerializer.Serialize(unlimited, Web), Web)!;

        Assert.That(back.ToDomain(key).IsUnlimited, Is.True);
    }

    // ── The closed vocabulary ────────────────────────────────────────────────

    /// <summary>
    /// Every reason the domain can produce has a wire code, and every wire code is in the published
    /// list.
    /// </summary>
    [Test]
    public void Every_degradation_reason_has_a_code_in_the_published_vocabulary()
    {
        var codes = Enum.GetValues<EntitlementDegradationReason>()
            .Select(EntitlementReasonCodes.Of)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(codes, Is.EquivalentTo(EntitlementReasonCodes.All));
            Assert.That(codes, Is.Unique);
            Assert.That(codes, Is.All.Matches<string>(code => code == code.ToLowerInvariant()),
                "codes are snake_case on the wire, matching the refusal codes the clients already read");
            Assert.That(codes, Has.None.Contains(" "));
        });
    }

    /// <summary>The server side of the unknown-code rule: it cannot emit one.</summary>
    [Test]
    public void A_code_outside_the_vocabulary_cannot_be_emitted()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => Degradation(reason: "guild_plan_limit_v2"), Throws.ArgumentException);
            Assert.That(() => Degradation(remedy: "call_support"), Throws.ArgumentException);
            Assert.That(EntitlementReasonCodes.IsKnown("guild_plan_limit_v2"), Is.False);
            Assert.That(EntitlementRemedyCodes.IsKnown(EntitlementRemedyCodes.BoostGuild), Is.True,
                "boost_guild is reserved now so its arrival is not a breaking change later");
        });
    }

    // ── The paired ceiling always says which side bound ──────────────────────

    [Test]
    public void A_paired_ceiling_without_a_side_is_not_a_degradation_that_can_be_built()
    {
        Assert.That(
            () => Degradation(reason: EntitlementReasonCodes.PairedCeiling, boundBy: null),
            Throws.ArgumentException.With.Message.Contains("which side"));
    }

    [Test]
    public void A_paired_ceiling_carries_the_side_that_bound()
    {
        var key = EntitlementKeys.VoiceVideoCeiling;
        var ladder = EntitlementLadders.VideoQuality;

        var degradation = EntitlementDegradationDto.From(
            key,
            EntitlementValue.OfRank(ladder.RankOf("1080p60")),
            EntitlementValue.OfRank(ladder.RankOf("720p30")),
            EntitlementDegradationReason.PairedCeiling,
            Guild,
            EntitlementRemedyPolicy.For(
                EntitlementDegradationReason.PairedCeiling, EntitlementBoundBy.Guild,
                instanceSellsUpgrades: true, actorCanManageGuild: false),
            EntitlementBoundBy.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(degradation.BoundBy, Is.EqualTo(EntitlementBoundBy.Guild));
            Assert.That(degradation.Remedy, Is.EqualTo(EntitlementRemedyCodes.UpgradeGuild));
            Assert.That(degradation.ActorCanRemedy, Is.False,
                "a member without ManageGuild gets the explanation and no button");
            Assert.That(degradation.Granted.Rung, Is.EqualTo("720p30"));
        });
    }

    /// <summary>The single-sided reasons fill their own side in, so one client code path handles all
    /// three and nothing has to special-case the absence.</summary>
    [Test]
    public void Single_sided_reasons_still_name_the_side_that_bound()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FromReason(EntitlementDegradationReason.GuildPlanLimit).BoundBy,
                Is.EqualTo(EntitlementBoundBy.Guild));
            Assert.That(FromReason(EntitlementDegradationReason.UserPlanLimit).BoundBy,
                Is.EqualTo(EntitlementBoundBy.User));
            Assert.That(FromReason(EntitlementDegradationReason.OperatorCeiling).BoundBy, Is.Null,
                "an operator ceiling is not a subject anybody can upgrade");
        });
    }

    /// <summary>The one reason that must never be rendered with an upgrade link, enforced where it
    /// is built rather than trusted to every enforcement site.</summary>
    [Test]
    public void An_operator_ceiling_cannot_be_sold_against()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => Degradation(
                    reason: EntitlementReasonCodes.OperatorCeiling,
                    remedy: EntitlementRemedyCodes.UpgradeGuild,
                    actorCanRemedy: true,
                    boundBy: null),
                Throws.ArgumentException);

            Assert.That(
                () => Degradation(remedy: EntitlementRemedyCodes.None, actorCanRemedy: true),
                Throws.ArgumentException.With.Message.Contains("no remedy"));
        });
    }

    // ── Who can fix it ───────────────────────────────────────────────────────

    [Test]
    public void An_instance_that_sells_nothing_offers_no_remedy_for_any_reason()
    {
        foreach (var reason in Enum.GetValues<EntitlementDegradationReason>())
        {
            var decision = EntitlementRemedyPolicy.For(
                reason, EntitlementBoundBy.Guild, instanceSellsUpgrades: false, actorCanManageGuild: true);

            Assert.That(decision, Is.EqualTo(EntitlementRemedyDecision.None),
                $"{reason} on a self-hosted instance would point at a service that is not deployed");
        }
    }

    [Test]
    public void The_lower_side_of_a_pair_decides_the_call_to_action()
    {
        var user = EntitlementRemedyPolicy.For(
            EntitlementDegradationReason.PairedCeiling, EntitlementBoundBy.User, true, actorCanManageGuild: true);
        var guild = EntitlementRemedyPolicy.For(
            EntitlementDegradationReason.PairedCeiling, EntitlementBoundBy.Guild, true, actorCanManageGuild: true);

        Assert.Multiple(() =>
        {
            Assert.That(user.Remedy, Is.EqualTo(EntitlementRemedyCodes.UpgradeUser));
            Assert.That(guild.Remedy, Is.EqualTo(EntitlementRemedyCodes.UpgradeGuild));
            Assert.That(
                () => EntitlementRemedyPolicy.For(
                    EntitlementDegradationReason.PairedCeiling, null, true, true),
                Throws.ArgumentException);
        });
    }

    // ── The domain record becomes the wire record without loss ───────────────

    /// <summary>
    /// Enforcement sites produce <see cref="EntitlementDegradation"/>, whose two values are
    /// formatted strings for the admin console.
    /// </summary>
    [Test]
    public void A_degradation_from_an_enforcement_site_keeps_both_values_as_numbers()
    {
        var key = EntitlementKeys.StorageUploadMaxBytes;

        var domain = EntitlementDegradation.IfReduced(
            key,
            EntitlementValue.OfNumber(41943040),
            EntitlementValue.OfNumber(26214400),
            EntitlementDegradationReason.GuildPlanLimit,
            detail: "plan free")!;

        var wire = EntitlementDegradationDto.From(
            domain, key, Guild,
            EntitlementRemedyPolicy.For(
                EntitlementDegradationReason.GuildPlanLimit, EntitlementBoundBy.Guild, true, true));

        var json = JsonNode.Parse(JsonSerializer.Serialize(wire, Web))!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(wire.Requested.Value, Is.EqualTo(41943040));
            Assert.That(wire.Granted.Value, Is.EqualTo(26214400));
            Assert.That(json.ContainsKey("detail"), Is.False,
                "Detail is admin-console-only and must not reach a member");
            Assert.That(json.ContainsKey("message"), Is.False,
                "the server never writes copy for a degradation; the codes are translation keys");
        });
    }

    // ── Hard denials speak the same language ─────────────────────────────────

    [Test]
    public void A_denial_uses_the_degradation_field_names_and_the_same_code()
    {
        var denial = EntitlementDenialDto.From(FromReason(EntitlementDegradationReason.GuildPlanLimit));

        var degradationFields = JsonNode.Parse(
            JsonSerializer.Serialize(FromReason(EntitlementDegradationReason.GuildPlanLimit), Web))!
            .AsObject().Select(field => field.Key).ToList();

        var denialFields = JsonNode.Parse(JsonSerializer.Serialize(denial, Web))!
            .AsObject().Select(field => field.Key).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(denialFields, Is.SupersetOf(degradationFields),
                "a refusal and a reduction are the same sentence, so they are the same fields");
            Assert.That(denial.Code, Is.EqualTo(denial.Reason));
            Assert.That(denial.Retryable, Is.False);
            Assert.That(EntitlementDenialDto.StatusCode, Is.EqualTo(403),
                "429 is retried three times and swallowed by the client; 401 signs the user out");
        });
    }

    /// <summary>An out-of-plan module has no countable ceiling, so it names the feature instead of
    /// sending an invented number. It is still the same code vocabulary, which is how a client tells
    /// it from a permission refusal at the call site.</summary>
    [Test]
    public void A_module_refusal_names_the_feature_and_carries_no_values()
    {
        var denial = new EntitlementDenialDto(
            EntitlementReasonCodes.GuildPlanLimit,
            "guild.features",
            EntitlementReasonCodes.GuildPlanLimit,
            EntitlementRemedyCodes.UpgradeGuild,
            actorCanRemedy: true,
            EntitlementSubjectDto.From(Guild),
            feature: "Forums");

        var json = JsonNode.Parse(JsonSerializer.Serialize(denial, Web))!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(json["feature"]!.GetValue<string>(), Is.EqualTo("Forums"));
            Assert.That(json.ContainsKey("requested"), Is.False);
            Assert.That(json.ContainsKey("granted"), Is.False);
        });
    }

    // ── Additive against v1 ──────────────────────────────────────────────────

    /// <summary>
    /// The whole basis of "a degradation is a 200 on the normal body": a response with nothing
    /// reduced has to be the bytes a v1 client already parses, and one with a degradation has to
    /// differ by exactly one property.
    /// </summary>
    [Test]
    public void Attaching_degradations_is_additive()
    {
        var body = new { roomId = "channel-123", version = 43 };
        var plain = JsonSerializer.Serialize(body, Web);

        var untouched = EntitlementResponses.WithDegradations(body, null, Web);
        var empty = EntitlementResponses.WithDegradations(body, [], Web);
        var degraded = EntitlementResponses.WithDegradations(
            body, [FromReason(EntitlementDegradationReason.GuildPlanLimit)], Web)!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(untouched!.ToJsonString(), Is.EqualTo(plain));
            Assert.That(empty!.ToJsonString(), Is.EqualTo(plain),
                "absent and empty mean the same thing, and absent is what v1 looked like");
            Assert.That(degraded.Select(field => field.Key),
                Is.EqualTo(new[] { "roomId", "version", "degradations" }));
            Assert.That(degraded["roomId"]!.GetValue<string>(), Is.EqualTo("channel-123"));
        });
    }

    [Test]
    public void A_body_that_is_not_an_object_has_nowhere_to_carry_a_degradation()
    {
        Assert.That(
            () => EntitlementResponses.WithDegradations(
                new[] { 1, 2, 3 }, [FromReason(EntitlementDegradationReason.GuildPlanLimit)], Web),
            Throws.InvalidOperationException);
    }

    // ── The snapshot ─────────────────────────────────────────────────────────

    [Test]
    public void A_snapshot_carries_only_the_keys_its_subject_can_hold()
    {
        var user = Snapshot(Member);

        Assert.Multiple(() =>
        {
            Assert.That(user.Entitlements.Keys, Contains.Item("user.max_devices"));
            Assert.That(user.Entitlements.Keys, Contains.Item("voice.video_ceiling"),
                "a paired key is the caller's own side of the pair and belongs on both snapshots");
            Assert.That(user.Entitlements.Keys, Has.None.EqualTo("guild.emoji_slots"));
        });
    }

    [Test]
    public void A_snapshot_echoes_its_subject_and_says_how_long_it_may_be_kept()
    {
        var snapshot = Snapshot(Guild);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Subject.Kind, Is.EqualTo("guild"));
            Assert.That(snapshot.Subject.Id, Is.EqualTo("guild-1"));
            Assert.That(snapshot.TtlSeconds, Is.GreaterThan(0));
            Assert.That(snapshot.VocabularyVersion, Is.EqualTo(EntitlementContract.VocabularyVersion));
            Assert.That(snapshot.LicenseMode, Is.EqualTo("hosted"));
        });
    }

    /// <summary>Without the rungs on the wire the client keeps a second copy of the ladder, and the
    /// day one gains a rung the two disagree about what a plan bought.</summary>
    [Test]
    public void A_snapshot_publishes_the_ladder_and_what_each_rung_permits()
    {
        var rungs = Snapshot(Guild).Ladders["video_quality"];

        Assert.Multiple(() =>
        {
            Assert.That(rungs.Select(rung => rung.Rung),
                Is.EqualTo(new[] { "none", "480p30", "720p30", "1080p30", "1080p60", "1440p60", "2160p60" }));
            Assert.That(rungs.Select(rung => rung.Rank), Is.Ordered);
            Assert.That(rungs, Is.All.Matches<EntitlementRungDto>(rung => rung.MaxHeight is not null),
                "every rung of the video ladder has to publish what it permits, or the client guesses");
            Assert.That(rungs.Last().MaxFramerate, Is.EqualTo(60));
        });
    }

    // ── The plan the numbers came from ───────────────────────────────────────

    /// <summary>The absence that has to stay an absence.</summary>
    [Test]
    public void A_snapshot_with_no_plan_names_none()
    {
        var snapshot = Snapshot(Guild);
        var json = JsonNode.Parse(JsonSerializer.Serialize(snapshot, Web))!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Plan, Is.Null);
            Assert.That(json.Select(field => field.Key), Has.None.EqualTo("plan"));
            Assert.That(json.Select(field => field.Key), Has.None.EqualTo("stripePublishableKey"),
                "absent, not null - both fields are additions and a v1 client must see what it saw");
        });
    }

    /// <summary>
    /// <b>A pinned subject reports their pin, never the current version.</b> Grandfathering exists
    /// so a plan's numbers can move without moving anybody already on it; naming the current version
    /// to somebody held at an older one describes limits they do not have, on the screen where they
    /// would act on it.
    /// </summary>
    [Test]
    public void A_pinned_subject_reports_the_version_it_is_actually_on()
    {
        var plan = EntitlementPlanDto.Resolve("pro@1", Versioned());

        Assert.Multiple(() =>
        {
            Assert.That(plan!.Name, Is.EqualTo("pro"));
            Assert.That(plan.Version, Is.EqualTo(1));
            Assert.That(plan.DisplayName, Is.EqualTo("Pro"),
                "a version is addressed as pro@1 and is never rendered as one");
        });
    }

    [Test]
    public void An_unpinned_subject_reports_the_current_version()
    {
        Assert.That(EntitlementPlanDto.Resolve("pro", Versioned())!.Version, Is.EqualTo(2));
    }

    /// <summary>
    /// <b>The pin is only readable next to the current version.</b> Both a grandfathered subject and
    /// one on the newest version report a number, so <c>version</c> alone cannot tell them apart -
    /// which means a client cannot honestly tell a grandfathered subscriber that they are
    /// grandfathered, the one thing the pin exists to make true. The comparison is the contract.
    /// </summary>
    [Test]
    public void A_grandfathered_subject_is_distinguishable_from_one_on_the_newest_version()
    {
        var pinned = EntitlementPlanDto.Resolve("pro@1", Versioned())!;
        var current = EntitlementPlanDto.Resolve("pro", Versioned())!;

        Assert.Multiple(() =>
        {
            Assert.That(pinned.Version, Is.LessThan(pinned.CurrentVersion));
            Assert.That(current.Version, Is.EqualTo(current.CurrentVersion));
            Assert.That(pinned.CurrentVersion, Is.EqualTo(2));
        });
    }

    /// <summary>Both halves are absent together on a configuration-only instance, which has never
    /// heard of a plan version. A v1 client sees exactly the object it saw before this field
    /// existed.</summary>
    [Test]
    public void An_unversioned_catalogue_reports_neither_version()
    {
        var plan = EntitlementPlanDto.Resolve("plus", Configured())!;

        Assert.Multiple(() =>
        {
            Assert.That(plan.Version, Is.Null);
            Assert.That(plan.CurrentVersion, Is.Null);
        });
    }

    /// <summary>Configuration-only instances have never heard of a plan version, and null there is
    /// the truth rather than a gap - there is exactly one version of a plan somebody wrote into a
    /// config file.</summary>
    [Test]
    public void An_unversioned_catalogue_reports_no_version()
    {
        var plan = EntitlementPlanDto.Resolve("plus", Configured());

        Assert.Multiple(() =>
        {
            Assert.That(plan!.Name, Is.EqualTo("plus"));
            Assert.That(plan.Version, Is.Null);
            Assert.That(JsonSerializer.Serialize(plan, Web),
                Is.EqualTo("""{"name":"plus","displayName":"Venta Plus"}"""));
        });
    }

    /// <summary>The display name is never null, so no client has to decide which of two fields to
    /// render. An operator who configured none gets the plan's own name, which is what they typed.
    /// </summary>
    [Test]
    public void A_plan_with_no_display_name_shows_its_own_name()
    {
        Assert.That(EntitlementPlanDto.Resolve("free", Configured())!.DisplayName, Is.EqualTo("free"));
    }

    /// <summary>The negative case.</summary>
    [Test]
    public void A_plan_the_catalogue_does_not_know_is_not_named()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EntitlementPlanDto.Resolve("ghost", Configured()), Is.Null);
            Assert.That(EntitlementPlanDto.Resolve(null, Configured()), Is.Null);
            Assert.That(EntitlementPlanDto.Resolve("  ", Configured()), Is.Null);
            Assert.That(EntitlementPlanDto.Resolve("plus", null), Is.Null);
        });
    }

    // ── The publishable key ──────────────────────────────────────────────────

    /// <summary>Server-supplied because the clients point at arbitrary instances at runtime: a key
    /// compiled into a build aims every self-hoster's checkout at whoever produced the build. Blank
    /// configuration is an absence rather than an empty string, so a client falling back to its
    /// bundled value has one thing to check.</summary>
    [Test]
    public void The_publishable_key_is_present_when_set_and_absent_when_blank()
    {
        var withKey = JsonNode.Parse(JsonSerializer.Serialize(
            Snapshot(Guild, stripePublishableKey: "pk_live_1"), Web))!.AsObject();
        var blank = JsonNode.Parse(JsonSerializer.Serialize(
            Snapshot(Guild, stripePublishableKey: "   "), Web))!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(withKey["stripePublishableKey"]!.GetValue<string>(), Is.EqualTo("pk_live_1"));
            Assert.That(blank.Select(field => field.Key), Has.None.EqualTo("stripePublishableKey"),
                "a blank key is no key, and no key is not an error");
        });
    }

    // ── The picker mapping, which is a pricing decision ──────────────────────

    [Test]
    public void A_request_the_ladder_cannot_express_clamps_to_the_top_rung()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VideoRungs.RungFor(EntitlementLadders.VideoQuality, 1440, 60), Is.EqualTo("1440p60"),
                "the client's picker has offered 1440p and 'source' since before the ladder had a rung "
                + "for either, so the mapping only stopped being a guess when the rungs landed");
            Assert.That(VideoRungs.RungFor(EntitlementLadders.VideoQuality, 4320, 120), Is.EqualTo("2160p60"),
                "a request above the whole ladder is still answered by the top rung plus a degradation");
            Assert.That(VideoRungs.RungFor(EntitlementLadders.VideoQuality, 720, 15), Is.EqualTo("720p30"),
                "a framerate below a rung's own is covered by it, which is what makes every 15 fps "
                + "option in the client's picker legal without a rung of its own");
            Assert.That(VideoRungs.Clamp("720p30", 1440, 60), Is.EqualTo((720, 30)));
            Assert.That(VideoRungs.Clamp("none", 1080, 30), Is.EqualTo((0, 0)),
                "over the video budget is an audio-only room, not a refusal");
        });
    }

    // ── The realtime envelope ────────────────────────────────────────────────

    [Test]
    public void The_change_event_carries_an_envelope_and_no_values()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(
            EntitlementsChangedDto.For(Guild, 7, ["voice.max_participants"]), Web))!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(json.Select(field => field.Key),
                Is.EquivalentTo(new[] { "subjectKind", "subjectId", "version", "changedKeys" }));
            Assert.That(EntitlementRealtimeEvents.Changed, Is.EqualTo("entitlements.Changed"));
            Assert.That(EntitlementsChangedDto.For(Guild, 7).ChangedKeys, Is.Empty,
                "changedKeys is advisory and a client must work without it");
        });
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static EntitlementSnapshotDto Snapshot(
        EntitlementSubject subject, EntitlementPlanDto? plan = null, string? stripePublishableKey = null) =>
        EntitlementSnapshotDto.From(
            EntitlementSet.Empty,
            subject,
            licenseMode: "hosted",
            upgradesAvailable: true,
            version: 3,
            ttlSeconds: 60,
            resolvedAt: DateTimeOffset.UnixEpoch,
            remedy: new EntitlementRemedyDecision(EntitlementRemedyCodes.UpgradeGuild, true),
            plan: plan,
            stripePublishableKey: stripePublishableKey);

    /// <summary>A configuration-shaped catalogue: names, no versions, and a display name on only one
    /// of them. What every instance running today has.</summary>
    private static PlanCatalogue Configured() =>
        PlanCatalogue.FromOptions(new EntitlementPlanOptions
        {
            Plans =
            {
                ["free"] = new Dictionary<string, string>(),
                ["plus"] = new Dictionary<string, string>(),
            },
            PlanDisplayNames = { ["plus"] = "Venta Plus" },
        });

    /// <summary>A Billing-shaped catalogue: the current version under the bare name and every live
    /// version under its <c>name@number</c> form, which is what grandfathering pins to.</summary>
    private static PlanCatalogue Versioned() =>
        new([
            Plan("pro", "Pro"),
            // No display name of their own, exactly as Billing writes them: pro@1 is a lookup key
            // and never something to show a member.
            Plan("pro@1"),
            Plan("pro@2"),
        ]);

    private static PlanDefinition Plan(string name, string? displayName = null) =>
        new(name, new Dictionary<EntitlementKey, EntitlementValue>(), displayName);

    private static EntitlementDegradationDto FromReason(EntitlementDegradationReason reason)
    {
        var boundBy = reason == EntitlementDegradationReason.UserPlanLimit
            ? EntitlementBoundBy.User
            : reason == EntitlementDegradationReason.OperatorCeiling
                ? null
                : EntitlementBoundBy.Guild;

        return EntitlementDegradationDto.From(
            EntitlementKeys.VoiceMaxParticipants,
            EntitlementValue.OfNumber(25),
            EntitlementValue.OfNumber(10),
            reason,
            reason == EntitlementDegradationReason.UserPlanLimit ? Member : Guild,
            EntitlementRemedyPolicy.For(reason, boundBy, instanceSellsUpgrades: true, actorCanManageGuild: true),
            boundBy);
    }

    private static EntitlementDegradationDto Degradation(
        string reason = EntitlementReasonCodes.GuildPlanLimit,
        string remedy = EntitlementRemedyCodes.UpgradeGuild,
        bool actorCanRemedy = true,
        string? boundBy = EntitlementBoundBy.Guild) =>
        new(
            EntitlementKeys.VoiceMaxParticipants.Name,
            EntitlementValueDto.Number(25),
            EntitlementValueDto.Number(10),
            reason,
            remedy,
            actorCanRemedy,
            EntitlementSubjectDto.From(Guild),
            boundBy);
}
