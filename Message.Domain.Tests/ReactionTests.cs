using Messaging.Domain.Entities;

namespace Message.Domain.Tests;

[TestFixture]
public class ReactionTests
{
    private static CreateReactionParams ValidParams(string emoji) => new()
    {
        MessageId = "msg-1",
        Emoji = emoji,
        UserId = "user-1",
        ConversationId = "conv-1"
    };

    // ── IsSingleEmoji: valid ────────────────────────────────────────────────

    [TestCase("😀", TestName = "Smiley_U1F600")]
    [TestCase("👍", TestName = "ThumbsUp_U1F44D")]
    [TestCase("🔥", TestName = "Fire_U1F525")]
    [TestCase("🎉", TestName = "Party_U1F389")]
    [TestCase("💯", TestName = "HundredPoints_U1F4AF")]
    [TestCase("🚀", TestName = "Rocket_U1F680")]
    [TestCase("🌍", TestName = "Globe_U1F30D")]
    [TestCase("😂", TestName = "LaughingCrying_U1F602")]
    [TestCase("🤔", TestName = "Thinking_U1F914")]
    [TestCase("🎮", TestName = "Gamepad_U1F3AE")]
    // Single-codepoint BMP emoji
    [TestCase("©", TestName = "Copyright_U00A9")]
    [TestCase("®", TestName = "Registered_U00AE")]
    [TestCase("⭐", TestName = "Star_U2B50")]
    [TestCase("⚡", TestName = "Lightning_U26A1")]
    [TestCase("✅", TestName = "CheckMark_U2705")]
    [TestCase("❌", TestName = "CrossMark_U274C")]
    [TestCase("❓", TestName = "Question_U2753")]
    [TestCase("♥", TestName = "Heart_U2665")]
    [TestCase("☑", TestName = "BallotCheck_U2611")]
    // Emoji + VS-16 variation selector (renders as emoji, single grapheme cluster)
    [TestCase("❤️", TestName = "RedHeart_WithVS16")]
    [TestCase("☀️", TestName = "Sun_WithVS16")]
    [TestCase("⭐️", TestName = "Star_WithVS16")]
    [TestCase("✔️", TestName = "CheckMark_WithVS16")]
    // Surrogate pairs — codepoints above U+FFFF need two UTF-16 chars (the "16-bit suckers")
    // string.Length == 2 for these, but they must still be treated as a single emoji
    [TestCase("😀", TestName = "SurrogatePair_Smiley_0x1F600")]
    [TestCase("👍", TestName = "SurrogatePair_ThumbsUp_0x1F44D")]
    [TestCase("🔥", TestName = "SurrogatePair_Fire_0x1F525")]
    [TestCase("🎉", TestName = "SurrogatePair_Party_0x1F389")]
    [TestCase("😂", TestName = "SurrogatePair_LaughingCrying_0x1F602")]
    [TestCase("🚀", TestName = "SurrogatePair_Rocket_0x1F680")]
    [TestCase("🤔", TestName = "SurrogatePair_Thinking_0x1F914")]
    [TestCase("🎮", TestName = "SurrogatePair_Gamepad_0x1F3AE")]
    // Skin tone modifier sequences (base emoji + Fitzpatrick modifier, single grapheme cluster)
    [TestCase("👍🏻", TestName = "ThumbsUp_LightSkin")]
    [TestCase("👍🏽", TestName = "ThumbsUp_MediumSkin")]
    [TestCase("👍🏿", TestName = "ThumbsUp_DarkSkin")]
    [TestCase("👋🏻", TestName = "WavingHand_LightSkin")]
    // Flag emojis (two regional indicator letters = one grapheme cluster)
    [TestCase("🇺🇸", TestName = "Flag_US")]
    [TestCase("🇬🇧", TestName = "Flag_GB")]
    [TestCase("🇯🇵", TestName = "Flag_JP")]
    // Keycap sequences (digit/symbol + VS-16 + U+20E3, single grapheme cluster)
    [TestCase("1️⃣", TestName = "Keycap_One")]
    [TestCase("0️⃣", TestName = "Keycap_Zero")]
    [TestCase("*️⃣", TestName = "Keycap_Asterisk")]
    [TestCase("#️⃣", TestName = "Keycap_Hash")]
    public void IsSingleEmoji_ValidEmoji_ReturnsTrue(string emoji)
    {
        Assert.That(Reaction.IsSingleEmoji(emoji), Is.True);
    }

    // Surrogate-pair strings have Length == 2, but must still count as one emoji
    [TestCase("😀", 2, TestName = "Smiley_LengthIsTwo")]
    [TestCase("🔥", 2, TestName = "Fire_LengthIsTwo")]
    [TestCase("🚀", 2, TestName = "Rocket_LengthIsTwo")]
    public void IsSingleEmoji_SurrogatePairEmoji_StringLengthIsTwoButStillValid(string emoji, int expectedLength)
    {
        Assert.Multiple(() =>
        {
            Assert.That(emoji.Length, Is.EqualTo(expectedLength), "surrogate pair occupies two UTF-16 chars");
            Assert.That(Reaction.IsSingleEmoji(emoji), Is.True);
        });
    }

    // ── IsSingleEmoji: invalid ──────────────────────────────────────────────

    [Test]
    public void IsSingleEmoji_Null_ReturnsFalse()
    {
        Assert.That(Reaction.IsSingleEmoji(null!), Is.False);
    }

    [TestCase("", TestName = "EmptyString")]
    [TestCase(" ", TestName = "Space")]
    [TestCase("   ", TestName = "MultipleSpaces")]
    // Plain ASCII — not emoji
    [TestCase("a", TestName = "SingleLetter")]
    [TestCase("A", TestName = "UppercaseLetter")]
    [TestCase("z", TestName = "LowercaseLetter")]
    [TestCase("abc", TestName = "Word")]
    [TestCase("hello world", TestName = "Sentence")]
    [TestCase("1", TestName = "Digit")]
    [TestCase("9", TestName = "Digit9")]
    [TestCase("123", TestName = "MultipleDigits")]
    [TestCase("!", TestName = "Exclamation")]
    [TestCase("@", TestName = "At")]
    [TestCase("$", TestName = "Dollar")]
    [TestCase("%", TestName = "Percent")]
    [TestCase("^", TestName = "Caret")]
    [TestCase("&", TestName = "Ampersand")]
    // Multiple emojis
    [TestCase("😀😀", TestName = "TwoIdenticalEmojis")]
    [TestCase("😀👍", TestName = "TwoDifferentEmojis")]
    [TestCase("😀🔥🎉", TestName = "ThreeEmojis")]
    // Emoji mixed with plain text
    [TestCase("a😀", TestName = "LetterThenEmoji")]
    [TestCase("😀a", TestName = "EmojiThenLetter")]
    [TestCase("hello😀", TestName = "WordThenEmoji")]
    [TestCase("😀world", TestName = "EmojiThenWord")]
    [TestCase("😀 😀", TestName = "TwoEmojisWithSpace")]
    // Two surrogate-pair emojis back-to-back — Length == 4, two grapheme clusters
    [TestCase("😀😀", TestName = "TwoSurrogatePairs_Identical")]
    [TestCase("😀👍", TestName = "TwoSurrogatePairs_Different")]
    [TestCase("🔥🎉", TestName = "TwoSurrogatePairs_FireParty")]
    public void IsSingleEmoji_InvalidInput_ReturnsFalse(string input)
    {
        Assert.That(Reaction.IsSingleEmoji(input), Is.False);
    }

    // ── Create: valid ───────────────────────────────────────────────────────

    [Test]
    public void Create_WithConversationId_SetsContextIdToConversationId()
    {
        var reaction = Reaction.Create(new CreateReactionParams
        {
            MessageId = "msg-1", Emoji = "😀", UserId = "user-1", ConversationId = "conv-1"
        });

        Assert.That(reaction.ContextId, Is.EqualTo("conv-1"));
    }

    [Test]
    public void Create_WithChannelIdOnly_SetsContextIdToChannelId()
    {
        var reaction = Reaction.Create(new CreateReactionParams
        {
            MessageId = "msg-1", Emoji = "😀", UserId = "user-1", ChannelId = "chan-1"
        });

        Assert.That(reaction.ContextId, Is.EqualTo("chan-1"));
    }

    [Test]
    public void Create_WithBothIds_PrefersConversationIdForContextId()
    {
        var reaction = Reaction.Create(new CreateReactionParams
        {
            MessageId = "msg-1", Emoji = "😀", UserId = "user-1",
            ConversationId = "conv-1", ChannelId = "chan-1"
        });

        Assert.That(reaction.ContextId, Is.EqualTo("conv-1"));
    }

    [Test]
    public void Create_SetsAllProperties()
    {
        var before = DateTime.UtcNow;

        var reaction = Reaction.Create(new CreateReactionParams
        {
            MessageId = "msg-1", Emoji = "😀", UserId = "user-1",
            ConversationId = "conv-1", ChannelId = "chan-1"
        });

        Assert.Multiple(() =>
        {
            Assert.That(reaction.MessageId, Is.EqualTo("msg-1"));
            Assert.That(reaction.Emoji, Is.EqualTo("😀"));
            Assert.That(reaction.UserId, Is.EqualTo("user-1"));
            Assert.That(reaction.ConversationId, Is.EqualTo("conv-1"));
            Assert.That(reaction.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(reaction.CreatedAt, Is.GreaterThanOrEqualTo(before));
        });
    }

    [Test]
    public void Create_WithChannelIdOnly_LeavesConversationIdNull()
    {
        var reaction = Reaction.Create(new CreateReactionParams
        {
            MessageId = "msg-1", Emoji = "😀", UserId = "user-1", ChannelId = "chan-1"
        });

        Assert.That(reaction.ConversationId, Is.Null);
    }

    [TestCase("😀", TestName = "WithSmiley")]
    [TestCase("🔥", TestName = "WithFire")]
    [TestCase("©", TestName = "WithCopyright")]
    [TestCase("😀", TestName = "WithSurrogatePairEmoji")]
    [TestCase("🇺🇸", TestName = "WithFlagEmoji")]
    public void Create_ValidEmoji_DoesNotThrow(string emoji)
    {
        Assert.DoesNotThrow(() => Reaction.Create(ValidParams(emoji)));
    }

    // ── Create: invalid ─────────────────────────────────────────────────────

    [Test]
    public void Create_NonEmojiString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Reaction.Create(ValidParams("hello")));
    }

    [Test]
    public void Create_MultipleEmojis_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Reaction.Create(ValidParams("😀😀")));
    }

    [Test]
    public void Create_EmptyEmoji_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Reaction.Create(ValidParams("")));
    }

    [Test]
    public void Create_WhitespaceEmoji_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Reaction.Create(ValidParams(" ")));
    }

    [Test]
    public void Create_NoConversationOrChannelId_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Reaction.Create(new CreateReactionParams
        {
            MessageId = "msg-1", Emoji = "😀", UserId = "user-1"
        }));
    }
    [Test]
    public void Create_With_EmptyConversationIdAndSetChannel_ShouldSetChannelId()
    {
        var reaction = Reaction.Create(new CreateReactionParams()
        {
            ChannelId = "chan-1",
            ConversationId = "",
            Emoji = "😀"
        });
        
        Assert.That(reaction.ContextId, Is.EqualTo("chan-1"));
    }
    [Test]
    public void Create_With_EmptyChannelIdAndSetChannel_ShouldSetConversationId()
    {
        var reaction = Reaction.Create(new CreateReactionParams()
        {
            ChannelId = "",
            ConversationId = "conv-1",
            Emoji = "😀"
        });
        
        Assert.That(reaction.ContextId, Is.EqualTo("conv-1"));
    }
}
