using System.Text;
using Guild.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;
using Messaging.Application.Handler.Messages;
using Messaging.Application.Services;
using Messaging.Application.Services.Privacy;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;

namespace Messaging.Tests.Services;

/// <summary>
/// T2-23. A recipient with <c>HidePushContent</c> gets routing ids and nothing else - no body, no
/// author name, no avatar, and no ciphertext, because a notification extension holding the
/// ciphertext can put the message on the lock screen just as well as the server could.
/// </summary>
[TestFixture]
public class PushContentPrivacyTests
{
    private static MessagePushPayload Payload(bool encrypted = false, params string[] hideFor) => new()
    {
        MessageId = "msg-1",
        ContextId = "conv-1",
        ConversationId = "conv-1",
        AuthorId = "author-1",
        SenderName = "Alice",
        SenderAvatarUrl = "https://example.invalid/a.png",
        IsEncrypted = encrypted,
        Content = encrypted ? "BASE64CIPHER"u8.ToArray() : "the actual message"u8.ToArray(),
        MlsGeneration = encrypted ? 2 : null,
        HideContentForUserIds = hideFor.ToHashSet(StringComparer.Ordinal),
    };

    [Test]
    public void ByDefault_TheFullPayloadIsUnchanged()
    {
        var data = MessagePushService.BuildData(Payload(), "user-2");

        Assert.Multiple(() =>
        {
            Assert.That(data["body"], Is.EqualTo("the actual message"));
            Assert.That(data["senderName"], Is.EqualTo("Alice"));
            Assert.That(data["authorId"], Is.EqualTo("author-1"));
            Assert.That(data.ContainsKey("hidden"), Is.False);
        });
    }

    [Test]
    public void HidingContent_StripsTheBodyTheNameTheAvatarAndTheAuthorId()
    {
        var data = MessagePushService.BuildData(Payload(hideFor: "user-2"), "user-2");

        Assert.Multiple(() =>
        {
            Assert.That(data["body"], Is.EqualTo(MessagePushService.HiddenContentPlaceholder));
            Assert.That(data.ContainsKey("senderName"), Is.False);
            Assert.That(data.ContainsKey("senderAvatarUrl"), Is.False);
            Assert.That(data.ContainsKey("authorId"), Is.False);
            Assert.That(data["hidden"], Is.EqualTo("1"));
        });
    }

    [Test]
    public void HidingContent_KeepsTheRoutingIds()
    {
        // The whole point of still sending the push: the client must be able to open the right
        // conversation once the user unlocks the phone.
        var data = MessagePushService.BuildData(Payload(hideFor: "user-2"), "user-2");

        Assert.Multiple(() =>
        {
            Assert.That(data["messageId"], Is.EqualTo("msg-1"));
            Assert.That(data["contextId"], Is.EqualTo("conv-1"));
            Assert.That(data["conversationId"], Is.EqualTo("conv-1"));
            Assert.That(data["recipientUserId"], Is.EqualTo("user-2"));
        });
    }

    [Test]
    public void HidingContent_DropsTheCiphertextToo()
    {
        // Shipping it would let the notification-service extension decrypt and render the body on
        // the lock screen, which is exactly what the setting exists to stop.
        var data = MessagePushService.BuildData(Payload(encrypted: true, hideFor: "user-2"), "user-2");

        Assert.Multiple(() =>
        {
            Assert.That(data.ContainsKey("ciphertext"), Is.False);
            Assert.That(data.ContainsKey("mlsGeneration"), Is.False);
            Assert.That(data["body"], Is.EqualTo(MessagePushService.HiddenContentPlaceholder));
        });
    }

    [Test]
    public void HidingIsPerRecipient_NotPerMessage()
    {
        var payload = Payload(hideFor: "user-2");

        var hidden = MessagePushService.BuildData(payload, "user-2");
        var normal = MessagePushService.BuildData(payload, "user-3");

        Assert.Multiple(() =>
        {
            Assert.That(hidden["body"], Is.EqualTo(MessagePushService.HiddenContentPlaceholder));
            Assert.That(normal["body"], Is.EqualTo("the actual message"));
        });
    }

    [Test]
    public void AnEncryptedMessageWithoutHiding_StillShipsItsCiphertext()
    {
        // Regression guard: the hidden path must not have swallowed the ordinary encrypted one.
        var data = MessagePushService.BuildData(Payload(encrypted: true), "user-2");

        Assert.Multiple(() =>
        {
            Assert.That(data["ciphertext"], Is.EqualTo("BASE64CIPHER"));
            Assert.That(data["body"], Is.EqualTo(MessagePushService.EncryptedPlaceholder));
        });
    }

    // ── The channel push path, where Guild pre-splits the cohorts ─────────────

    private static ChannelPushRequested ChannelPush(bool hideContent) => new()
    {
        GuildId = "guild-1",
        ChannelId = "chan-1",
        MessageId = "msg-1",
        UserIds = ["user-2"],
        AuthorId = hideContent ? string.Empty : "author-1",
        Content = hideContent ? [] : "hello"u8.ToArray(),
        HideContent = hideContent,
    };

    [Test]
    public async Task ChannelPush_WithTheHiddenFlag_MakesNoProfileLookup()
    {
        // Guild blanks AuthorId on that cohort, so there is nothing to resolve - and asking anyway
        // would be a bus call for a name that may not be rendered.
        var context = new TestMessagingContext(Guid.NewGuid().ToString());
        try
        {
            var bus = new FakeMessageBus(msg => msg switch
            {
                GetPushTokensForUsersRequest => new GetPushTokensForUsersResponse { Tokens = [] },
                GetProfileByUserIdRequest => throw new InvalidOperationException("must not be asked"),
                _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
            });

            var privacy = TestPrivacyServices.Build(bus);

            await ChannelPushRequestedHandler.Handle(
                ChannelPush(hideContent: true), bus, privacy.Blocks, privacy.Privacy,
                new EfCoreMessageRepository(context), NullLogger<ChannelPushRequestedHandler>.Instance);

            Assert.That(bus.Invoked.OfType<GetProfileByUserIdRequest>(), Is.Empty);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    [Test]
    public async Task ChannelPush_WithABlankAuthorId_StillRecoversItToApplyBlocks()
    {
        // A contentless notification from somebody you blocked is still a notification from
        // somebody you blocked.
        var context = new TestMessagingContext(Guid.NewGuid().ToString());
        try
        {
            var repo = new EfCoreMessageRepository(context);
            await repo.CreateMessageAsync(Message.Create(new CreateMessageParams
            {
                Content = "hello"u8.ToArray(),
                ChannelId = "chan-1",
                AuthorId = "author-1",
            }));
            await context.SaveChangesAsync();

            var stored = await context.Messages.FirstAsync();

            var bus = new FakeMessageBus(msg => msg switch
            {
                GetPushTokensForUsersRequest => throw new InvalidOperationException("must not be asked"),
                _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
            });

            var privacy = TestPrivacyServices.Build(bus,
                blocks: [new BlockRelationship { BlockerId = "user-2", BlockedId = "author-1" }]);

            var request = ChannelPush(hideContent: true);
            request.MessageId = stored.Id;

            await ChannelPushRequestedHandler.Handle(
                request, bus, privacy.Blocks, privacy.Privacy, repo,
                NullLogger<ChannelPushRequestedHandler>.Instance);

            Assert.That(bus.Invoked.OfType<GetPushTokensForUsersRequest>(), Is.Empty,
                "The only recipient blocked the author, so nothing should have been sent at all");
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    [Test]
    public async Task ChannelPush_WithoutTheFlag_ResolvesTheAuthorAsBefore()
    {
        var context = new TestMessagingContext(Guid.NewGuid().ToString());
        try
        {
            var bus = new FakeMessageBus(msg => msg switch
            {
                GetPushTokensForUsersRequest => new GetPushTokensForUsersResponse
                {
                    Tokens = [new PushTokenResponse { UserId = "user-2", Token = "tok", Kind = PushTokenKind.Fcm }],
                },
                GetProfileByUserIdRequest => new GetProfileByUserIdResponse
                {
                    Profile = new ProfileDto { UserId = "author-1", UserName = "Alice", Relationships = [] },
                },
                _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
            });

            var privacy = TestPrivacyServices.Build(bus);

            await ChannelPushRequestedHandler.Handle(
                ChannelPush(hideContent: false), bus, privacy.Blocks, privacy.Privacy,
                new EfCoreMessageRepository(context), NullLogger<ChannelPushRequestedHandler>.Instance);

            Assert.That(bus.Invoked.OfType<GetProfileByUserIdRequest>().Count(), Is.EqualTo(1));
        }
        finally
        {
            await context.DisposeAsync();
        }
    }
}
