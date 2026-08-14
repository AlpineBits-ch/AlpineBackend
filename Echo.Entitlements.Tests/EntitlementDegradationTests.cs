using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;

namespace Echo.Entitlements.Tests;

/// <summary>Degrade, do not deny (spec section 3.3).</summary>
[TestFixture]
public class EntitlementDegradationTests
{
    [Test]
    public void A_request_beyond_the_ceiling_is_reduced_and_says_which_side_bound()
    {
        var degradation = EntitlementDegradation.IfReduced(
            EntitlementKeys.VoiceVideoCeiling,
            EntitlementKeys.VoiceVideoCeiling.Parse("1080p60"),
            EntitlementKeys.VoiceVideoCeiling.Parse("720p30"),
            EntitlementDegradationReason.GuildPlanLimit);

        Assert.That(degradation, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(degradation!.Key, Is.EqualTo("voice.video_ceiling"));
            Assert.That(degradation.Requested, Is.EqualTo("1080p60"));
            Assert.That(degradation.Granted, Is.EqualTo("720p30"),
                "the reduced value is rendered as a rung, not a rank - a client cannot map a number "
                + "back onto the ladder");
            Assert.That(degradation.Reason, Is.EqualTo(EntitlementDegradationReason.GuildPlanLimit));
        });
    }

    [Test]
    public void A_request_that_fits_is_not_a_degradation()
    {
        var degradation = EntitlementDegradation.IfReduced(
            EntitlementKeys.StorageUploadMaxBytes,
            EntitlementValue.OfNumber(1_000),
            EntitlementValue.OfNumber(26_214_400),
            EntitlementDegradationReason.GuildPlanLimit);

        Assert.That(degradation, Is.Null);
    }

    [Test]
    public void A_request_for_exactly_the_ceiling_is_not_a_degradation()
    {
        var degradation = EntitlementDegradation.IfReduced(
            EntitlementKeys.VoiceMaxParticipants,
            EntitlementValue.OfNumber(10),
            EntitlementValue.OfNumber(10),
            EntitlementDegradationReason.GuildPlanLimit);

        Assert.That(degradation, Is.Null,
            "a member who asked for exactly their ceiling got what they asked for, and a banner "
            + "there would be noise on every single join");
    }

    [Test]
    public void A_flag_that_was_refused_reads_as_a_degradation_to_false()
    {
        var degradation = EntitlementDegradation.IfReduced(
            EntitlementKeys.GuildVanityUrl,
            EntitlementValue.OfFlag(true),
            EntitlementValue.OfFlag(false),
            EntitlementDegradationReason.GuildPlanLimit);

        Assert.That(degradation, Is.Not.Null);
        Assert.That(degradation!.Granted, Is.EqualTo("false"));
    }

    [Test]
    public void The_operator_ceiling_is_a_distinct_reason_from_a_plan_limit()
    {
        var degradation = EntitlementDegradation.IfReduced(
            EntitlementKeys.VoiceMaxParticipants,
            EntitlementValue.OfNumber(50),
            EntitlementValue.OfNumber(8),
            EntitlementDegradationReason.OperatorCeiling,
            "VOICE_MAX_PARTICIPANTS");

        Assert.That(degradation, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(degradation!.Reason, Is.EqualTo(EntitlementDegradationReason.OperatorCeiling),
                "on a self-hosted instance no upgrade changes this, so it must never be rendered "
                + "with an upgrade link");
            Assert.That(degradation.Detail, Is.EqualTo("VOICE_MAX_PARTICIPANTS"));
        });
    }

    [Test]
    public void A_value_of_the_wrong_shape_for_the_key_is_refused()
    {
        Assert.That(() => EntitlementDegradation.IfReduced(
                EntitlementKeys.VoiceMaxParticipants,
                EntitlementValue.OfFlag(true),
                EntitlementValue.OfNumber(10),
                EntitlementDegradationReason.GuildPlanLimit),
            Throws.InstanceOf<ArgumentException>());
    }
}
