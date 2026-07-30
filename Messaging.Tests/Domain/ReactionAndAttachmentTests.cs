using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Reactions;

namespace Messaging.Tests.Domain;

/// <summary>
/// Covers a handful of small pure-logic branches left uncovered by the endpoint/handler tests
/// elsewhere in this suite: Reaction.Create's two validation-guard throw paths, Attachment.Create's
/// auto-generated-id fallback (every other test in this suite always supplies an explicit Id), and
/// the domain events' ToString() overrides (used for logging, never exercised just by raising the
/// event).
/// </summary>
[TestFixture]
public class ReactionAndAttachmentTests
{
    [Test]
    public void Reaction_Create_CustomEmojiWithoutName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Reaction.Create(new CreateReactionParams
        {
            MessageId = "msg-1",
            UserId = "user-1",
            Emoji = "",
            EmojiId = "custom-1",
            ConversationId = "conv-1",
        }));
    }

    [Test]
    public void Reaction_Create_NeitherConversationNorChannelId_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Reaction.Create(new CreateReactionParams
        {
            MessageId = "msg-1",
            UserId = "user-1",
            Emoji = "👍",
        }));
    }

    [Test]
    public void Attachment_Create_NoIdSupplied_GeneratesOne()
    {
        var attachment = Attachment.Create(new CreateAttachmentParams
        {
            FileName = "photo.png",
            ContentType = "image/png",
            SizeBytes = 100,
            Url = "https://cdn/x",
            CreatorId = "user-1",
        });

        Assert.That(attachment.Id, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Message_Create_GuildMemberLeave_PicksASystemMessageVariant()
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = ""u8.ToArray(),
            ChannelId = "chan-1",
            AuthorId = "author-1",
            Type = MessageType.GuildMemberLeave,
        });

        Assert.That(message.SystemMessageVariant, Is.Not.Null.And.InRange(0, SystemMessageVariants.GuildMemberLeaveCount - 1));
    }

    [Test]
    public void ReactionCreated_ToString_IncludesKeyFields()
    {
        var evt = new ReactionCreated { MessageId = "msg-1", Emoji = "👍", UserId = "user-1" };

        Assert.That(evt.ToString(), Does.Contain("msg-1").And.Contain("user-1"));
    }

    [Test]
    public void ReactionRemoved_ToString_IncludesMessageId()
    {
        var evt = new ReactionRemoved { MessageId = "msg-1", Emoji = "👍", UserId = "user-1" };

        Assert.That(evt.ToString(), Does.Contain("msg-1"));
    }
}
