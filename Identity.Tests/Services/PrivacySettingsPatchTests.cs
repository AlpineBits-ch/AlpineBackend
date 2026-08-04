using System.Text.Json;
using Identity.Application.Services;
using Identity.Domain.Entities;
using Domain;
using Identity.Domain.Enums;

namespace Identity.Tests.Services;

/// <summary>
/// The partial-update applier behind <c>PATCH api/v1/privacy-settings</c>.
///
/// <para>Unit-tested apart from the endpoint because the interesting behaviour is entirely in the
/// parse-and-apply step: which bodies are refused, which fields count as changed, and - most
/// importantly - that a refused body leaves the entity <b>untouched</b>. A partially applied privacy
/// patch is worse than a rejected one, because the client is told the write failed while some of it
/// landed.</para>
/// </summary>
[TestFixture]
public class PrivacySettingsPatchTests
{
    private UserPrivacySettings _settings = null!;

    [SetUp]
    public void SetUp() => _settings = UserPrivacySettings.CreateDefault("user_1", DateTimeOffset.UtcNow);

    private PrivacySettingsPatch.Result Apply(string json) =>
        PrivacySettingsPatch.Apply(JsonSerializer.Deserialize<JsonElement>(json), _settings);

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public void Apply_SetsEveryWritableField()
    {
        var result = Apply("""
            {
              "allowDataCollection": true,
              "allowPersonalization": true,
              "allowVoiceRecordingInClips": true,
              "directMessagePolicy": "Nobody",
              "friendRequestPolicy": "FriendsOfFriends",
              "discoverableByUsername": false,
              "discoverableByEmail": true,
              "discoverableByPhone": true,
              "mutualServersVisibility": "Everyone",
              "mutualFriendsVisibility": "Nobody",
              "connectionsVisibility": "Everyone",
              "birthdayVisibility": "Friends",
              "shareActivity": false,
              "allowPositionalVoiceCapture": false,
              "sendReadReceipts": false,
              "sendTypingIndicators": false,
              "dmRetentionDays": 30,
              "explicitContentFilter": "Everyone",
              "hidePushContent": true
            }
            """);

        Assert.That(result.Ok, Is.True, result.Error);
        Assert.Multiple(() =>
        {
            Assert.That(_settings.AllowDataCollection, Is.True);
            Assert.That(_settings.AllowPersonalization, Is.True);
            Assert.That(_settings.AllowVoiceRecordingInClips, Is.True);
            Assert.That(_settings.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Nobody));
            Assert.That(_settings.FriendRequestPolicy, Is.EqualTo(FriendRequestPolicy.FriendsOfFriends));
            Assert.That(_settings.DiscoverableByUsername, Is.False);
            Assert.That(_settings.DiscoverableByEmail, Is.True);
            Assert.That(_settings.DiscoverableByPhone, Is.True);
            Assert.That(_settings.MutualServersVisibility, Is.EqualTo(Visibility.Everyone));
            Assert.That(_settings.MutualFriendsVisibility, Is.EqualTo(Visibility.Nobody));
            Assert.That(_settings.ConnectionsVisibility, Is.EqualTo(Visibility.Everyone));
            Assert.That(_settings.BirthdayVisibility, Is.EqualTo(Visibility.Friends));
            Assert.That(_settings.ShareActivity, Is.False);
            Assert.That(_settings.AllowPositionalVoiceCapture, Is.False);
            Assert.That(_settings.SendReadReceipts, Is.False);
            Assert.That(_settings.SendTypingIndicators, Is.False);
            Assert.That(_settings.DmRetentionDays, Is.EqualTo(30));
            Assert.That(_settings.ExplicitContentFilter, Is.EqualTo(ExplicitContentFilter.Everyone));
            Assert.That(_settings.HidePushContent, Is.True);
        });

        Assert.That(result.ChangedFields, Has.Count.EqualTo(PrivacySettingsPatch.WritableFieldNames.Count),
            "every writable field moved, so every one of them must be reported as changed");
    }

    [Test]
    public void Apply_OmittedFieldsAreLeftAlone()
    {
        _settings.HidePushContent = true;
        _settings.DmRetentionDays = 7;

        var result = Apply("""{"shareActivity": false}""");

        Assert.That(result.Ok, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(_settings.ShareActivity, Is.False);
            Assert.That(_settings.HidePushContent, Is.True, "an omitted field is not a field set to its default");
            Assert.That(_settings.DmRetentionDays, Is.EqualTo(7));
            Assert.That(result.ChangedFields, Is.EqualTo(new[] { "shareActivity" }));
        });
    }

    [Test]
    public void Apply_AcceptsEnumNamesCaseInsensitively()
    {
        var result = Apply("""{"directMessagePolicy": "friendsandservermembers"}""");

        Assert.That(result.Ok, Is.True, result.Error);
        Assert.That(_settings.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.FriendsAndServerMembers));
    }

    [Test]
    public void Apply_AcceptsTheNumericFormOfAnEnum()
    {
        // A client generated from the OpenAPI document may send the ordinal.
        var result = Apply("""{"birthdayVisibility": 2}""");

        Assert.That(result.Ok, Is.True, result.Error);
        Assert.That(_settings.BirthdayVisibility, Is.EqualTo(Visibility.Nobody));
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public void Apply_EmptyObject_ChangesNothing()
    {
        var result = Apply("{}");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.ChangedFields, Is.Empty);
            Assert.That(_settings.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Friends));
        });
    }

    [Test]
    public void Apply_ReWritingTheCurrentValue_IsNotAChange()
    {
        // The caller uses ChangedFields to decide whether to bump the version, write an audit row and
        // publish a cache-eviction event. A client that re-posts the state it just read must not
        // cause any of those.
        var result = Apply("""{"shareActivity": true, "directMessagePolicy": "Friends"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.ChangedFields, Is.Empty);
        });
    }

    [Test]
    public void Apply_DmRetentionDaysNull_ClearsTheWindow()
    {
        _settings.DmRetentionDays = 30;

        var result = Apply("""{"dmRetentionDays": null}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.Error);
            Assert.That(_settings.DmRetentionDays, Is.Null,
                "explicit null is a value here ('keep forever'), not an omission");
            Assert.That(result.ChangedFields, Is.EqualTo(new[] { "dmRetentionDays" }));
        });
    }

    [Test]
    public void Apply_DmRetentionDaysAtTheBoundaries_IsAccepted()
    {
        Assert.That(Apply("""{"dmRetentionDays": 1}""").Ok, Is.True);
        Assert.That(_settings.DmRetentionDays, Is.EqualTo(1));

        Assert.That(Apply($$"""{"dmRetentionDays": {{PrivacySettingsPatch.MaxDmRetentionDays}}}""").Ok, Is.True);
        Assert.That(_settings.DmRetentionDays, Is.EqualTo(PrivacySettingsPatch.MaxDmRetentionDays));
    }

    [Test]
    public void Apply_FieldNamesAreMatchedCaseInsensitively()
    {
        var result = Apply("""{"HidePushContent": true}""");

        Assert.That(result.Ok, Is.True, result.Error);
        Assert.That(_settings.HidePushContent, Is.True);
        Assert.That(result.ChangedFields, Is.EqualTo(new[] { "hidePushContent" }),
            "the canonical camelCase name is what gets reported, whatever casing arrived");
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public void Apply_UnknownField_IsRefused()
    {
        var result = Apply("""{"allowEverything": true}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Does.Contain("allowEverything"));
            Assert.That(result.ChangedFields, Is.Empty);
        });
    }

    [Test]
    public void Apply_UnknownFieldAlongsideAValidOne_AppliesNeither()
    {
        // The case that makes the unknown-field rule worth having. A client that misspells one key
        // and gets a 200 believes the whole request landed.
        var result = Apply("""{"hidePushContent": true, "hidePushContnet": true}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(_settings.HidePushContent, Is.False,
                "a refused patch must leave the entity exactly as it found it");
        });
    }

    [Test]
    public void Apply_InvalidValueAfterAValidField_AppliesNeither()
    {
        var result = Apply("""{"shareActivity": false, "directMessagePolicy": "Anyone"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Does.Contain("directMessagePolicy"));
            Assert.That(_settings.ShareActivity, Is.True, "the valid field must not have been applied");
        });
    }

    [Test]
    public void Apply_UnknownEnumMember_IsRefused()
    {
        var result = Apply("""{"explicitContentFilter": "Maximum"}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(_settings.ExplicitContentFilter, Is.EqualTo(ExplicitContentFilter.UnknownSenders));
        });
    }

    [Test]
    public void Apply_OutOfRangeNumericEnum_IsRefused()
    {
        // An unchecked cast would produce a value no branch of any enforcement table matches, which
        // fails *open* wherever the default arm is the permissive one.
        var result = Apply("""{"friendRequestPolicy": 99}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(_settings.FriendRequestPolicy, Is.EqualTo(FriendRequestPolicy.Everyone));
        });
    }

    [Test]
    public void Apply_NullForAConsentFlag_IsRefused()
    {
        var result = Apply("""{"allowDataCollection": null}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(_settings.AllowDataCollection, Is.False,
                "reading null as 'withdraw consent' would be a guess about intent");
        });
    }

    [Test]
    public void Apply_StringForABoolean_IsRefused()
    {
        var result = Apply("""{"hidePushContent": "true"}""");

        Assert.That(result.Ok, Is.False);
        Assert.That(_settings.HidePushContent, Is.False);
    }

    [TestCase("""{"dmRetentionDays": 0}""")]
    [TestCase("""{"dmRetentionDays": -1}""")]
    [TestCase("""{"dmRetentionDays": 100000}""")]
    [TestCase("""{"dmRetentionDays": 1.5}""")]
    [TestCase("""{"dmRetentionDays": "30"}""")]
    public void Apply_InvalidRetentionWindow_IsRefused(string body)
    {
        var result = Apply(body);

        Assert.That(result.Ok, Is.False);
        Assert.That(_settings.DmRetentionDays, Is.Null);
    }

    [TestCase("[]")]
    [TestCase("\"nope\"")]
    [TestCase("42")]
    [TestCase("null")]
    public void Apply_NonObjectRoot_IsRefused(string body)
    {
        var result = Apply(body);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("object"));
    }

    [Test]
    public void Apply_DuplicateKey_IsRefused()
    {
        // System.Text.Json keeps both properties when a document repeats a key, and "last one wins"
        // is a convention, not a guarantee. A privacy write that depends on which duplicate the
        // parser happened to hand over last is a write nobody can reason about.
        var result = Apply("""{"hidePushContent": true, "hidePushContent": false}""");

        Assert.That(result.Ok, Is.False);
        Assert.That(_settings.HidePushContent, Is.False);
    }

    [Test]
    public void WritableFieldNames_DoesNotIncludeVersion()
    {
        Assert.That(PrivacySettingsPatch.WritableFieldNames, Does.Not.Contain("version"),
            "Version is the server's monotonic counter; a client that could set it could replay an "
            + "old state past every consumer's cache check");
    }
}
