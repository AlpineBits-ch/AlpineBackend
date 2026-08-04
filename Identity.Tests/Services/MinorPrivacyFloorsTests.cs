using System.Text.Json;
using Identity.Application.Services;
using Identity.Domain.Entities;
using Domain;
using Identity.Domain.Enums;

namespace Identity.Tests.Services;

/// <summary>
/// T1-11. <c>AgeVerification.BirthDate</c> was captured at registration and drove nothing at all;
/// these are the floors it now drives.
/// </summary>
[TestFixture]
public class MinorPrivacyFloorsTests
{
    private static UserPrivacySettings Wide() => new()
    {
        Id = "upvs_test",
        UserId = "user_test",
        DirectMessagePolicy = DirectMessagePolicy.Everyone,
        AllowPersonalization = true,
        DiscoverableByEmail = true,
        DiscoverableByPhone = true,
        AllowVoiceRecordingInClips = true,
        ExplicitContentFilter = ExplicitContentFilter.Off,
        HidePushContent = true,
        Version = 4,
    };

    private static PrivacySettingsPatch.Result Apply(string json, bool isMinor, UserPrivacySettings? into = null) =>
        PrivacySettingsPatch.Apply(
            JsonSerializer.Deserialize<JsonElement>(json),
            into ?? UserPrivacySettings.CreateDefault("user_test", DateTimeOffset.UtcNow),
            isMinor);

    // ── negative: each floor refuses the write that would breach it ─────────

    [TestCase("""{"directMessagePolicy":"Everyone"}""", "directMessagePolicy")]
    [TestCase("""{"allowPersonalization":true}""", "allowPersonalization")]
    [TestCase("""{"discoverableByEmail":true}""", "discoverableByEmail")]
    [TestCase("""{"discoverableByPhone":true}""", "discoverableByPhone")]
    [TestCase("""{"allowVoiceRecordingInClips":true}""", "allowVoiceRecordingInClips")]
    [TestCase("""{"explicitContentFilter":"Off"}""", "explicitContentFilter")]
    public void Patch_AsAMinor_RefusesTheWriteAndNamesTheField(string json, string expectedField)
    {
        var result = Apply(json, isMinor: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.RestrictedField, Is.EqualTo(expectedField),
                "the refusal has to name the field - a client told only 'forbidden' has to guess "
                + "which of nineteen fields to stop sending");
            Assert.That(result.ChangedFields, Is.Empty);
        });
    }

    [Test]
    public void Patch_AsAMinor_RefusingOneFieldAppliesNoneOfTheOthers()
    {
        var settings = UserPrivacySettings.CreateDefault("user_test", DateTimeOffset.UtcNow);

        var result = Apply("""{"hidePushContent":true,"allowPersonalization":true}""", isMinor: true, settings);

        Assert.Multiple(() =>
        {
            Assert.That(result.RestrictedField, Is.EqualTo("allowPersonalization"));
            Assert.That(settings.HidePushContent, Is.False,
                "a patch is all-or-nothing; the permitted half of a refused body must not land");
        });
    }

    // ── normal: the same writes succeed for an adult ────────────────────────

    [TestCase("""{"directMessagePolicy":"Everyone"}""")]
    [TestCase("""{"allowPersonalization":true}""")]
    [TestCase("""{"discoverableByEmail":true}""")]
    [TestCase("""{"explicitContentFilter":"Off"}""")]
    public void Patch_AsAnAdult_AllowsEveryOneOfThem(string json)
    {
        var result = Apply(json, isMinor: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.RestrictedField, Is.Null);
            Assert.That(result.ChangedFields, Is.Not.Empty);
        });
    }

    // ── edge: only the widening direction is refused ────────────────────────

    [TestCase("""{"allowPersonalization":false}""")]
    [TestCase("""{"discoverableByEmail":false}""")]
    [TestCase("""{"discoverableByPhone":false}""")]
    [TestCase("""{"allowVoiceRecordingInClips":false}""")]
    [TestCase("""{"explicitContentFilter":"Everyone"}""")]
    [TestCase("""{"directMessagePolicy":"Nobody"}""")]
    [TestCase("""{"directMessagePolicy":"FriendsAndServerMembers"}""")]
    public void Patch_AsAMinor_AllowsValuesThatAgreeWithOrExceedTheFloor(string json)
    {
        var result = Apply(json, isMinor: true);

        Assert.That(result.RestrictedField, Is.Null,
            "refusing a value that is already at or beyond the floor would break any client that "
            + "PATCHes back the whole state it just read");
    }

    [Test]
    public void Patch_AsAMinor_LeavesUnrelatedFieldsCompletelyAlone()
    {
        var settings = UserPrivacySettings.CreateDefault("user_test", DateTimeOffset.UtcNow);

        var result = Apply("""{"hidePushContent":true,"sendReadReceipts":false,"dmRetentionDays":30}""",
            isMinor: true, settings);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(settings.HidePushContent, Is.True);
            Assert.That(settings.SendReadReceipts, Is.False);
            Assert.That(settings.DmRetentionDays, Is.EqualTo(30));
        });
    }

    // ── the clamp half ──────────────────────────────────────────────────────

    [Test]
    public void Snapshot_ForAMinor_NarrowsEveryFloorWithoutTouchingTheOriginal()
    {
        var stored = Wide();

        var reported = MinorPrivacyFloors.Snapshot(stored, isMinor: true);

        Assert.Multiple(() =>
        {
            Assert.That(reported.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Friends));
            Assert.That(reported.AllowPersonalization, Is.False);
            Assert.That(reported.DiscoverableByEmail, Is.False);
            Assert.That(reported.DiscoverableByPhone, Is.False);
            Assert.That(reported.AllowVoiceRecordingInClips, Is.False);
            Assert.That(reported.ExplicitContentFilter, Is.EqualTo(ExplicitContentFilter.UnknownSenders));

            // Everything outside the floors is carried through untouched.
            Assert.That(reported.HidePushContent, Is.True);
            Assert.That(reported.Version, Is.EqualTo(4));
        });

        Assert.Multiple(() =>
        {
            // This is the assertion that keeps "unlocked on the birthday" true.
            Assert.That(stored.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Everyone));
            Assert.That(stored.AllowPersonalization, Is.True);
            Assert.That(stored.ExplicitContentFilter, Is.EqualTo(ExplicitContentFilter.Off));
        });
    }

    [Test]
    public void Snapshot_ForAnAdult_ReturnsTheRecordUnchanged()
    {
        var stored = Wide();

        var reported = MinorPrivacyFloors.Snapshot(stored, isMinor: false);

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.SameAs(stored));
            Assert.That(reported.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Everyone));
            Assert.That(reported.AllowPersonalization, Is.True);
        });
    }

    [Test]
    public void ClampOnTheBusSummary_AppliesTheSameFloors()
    {
        // Messaging resolves the DM policy from this projection, not from the REST endpoint.
        var summary = PrivacySettingsMapping.ToSummary(Wide());

        MinorPrivacyFloors.Clamp(summary, isMinor: true);

        Assert.Multiple(() =>
        {
            Assert.That(summary.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Friends));
            Assert.That(summary.AllowPersonalization, Is.False);
            Assert.That(summary.DiscoverableByEmail, Is.False);
            Assert.That(summary.DiscoverableByPhone, Is.False);
            Assert.That(summary.AllowVoiceRecordingInClips, Is.False);
            Assert.That(summary.ExplicitContentFilter, Is.EqualTo(ExplicitContentFilter.UnknownSenders));
        });
    }
}
