using Facet.Extensions;
using Messaging.Application.Dtos.Response;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;

namespace Messaging.Tests.Dtos;

/// <summary>
/// Covers MessageMapConfig, which derives the HAS_THREAD bit from Message.ThreadId at projection
/// time so no second writable copy of the relationship exists to drift from it.
/// </summary>
[TestFixture]
public class MessageDtoFlagTests
{
    private static Message MakeMessage() => Message.Create(new CreateMessageParams
    {
        Content = "hello"u8.ToArray(),
        ChannelId = "chan-1",
        AuthorId = "author-1",
    });

    [Test]
    public void NoThread_LeavesHasThreadClear()
    {
        var dto = MakeMessage().ToFacet<Message, MessageDto>();

        Assert.That(MessageFlags.Has(dto.Flags, MessageFlags.HasThread), Is.False);
    }

    [Test]
    public void WithThread_SetsHasThread()
    {
        var message = MakeMessage();
        message.ThreadId = "chan-thread";

        var dto = message.ToFacet<Message, MessageDto>();

        Assert.That(MessageFlags.Has(dto.Flags, MessageFlags.HasThread), Is.True);
    }

    [Test]
    public void WithThread_KeepsTheStoredBits()
    {
        // Derived, not overwritten: a message that suppressed its embeds keeps saying so.
        var message = MakeMessage();
        message.ThreadId = "chan-thread";
        message.Flags = MessageFlags.SuppressEmbeds;

        var dto = message.ToFacet<Message, MessageDto>();

        Assert.That(MessageFlags.Has(dto.Flags, MessageFlags.SuppressEmbeds), Is.True);
        Assert.That(MessageFlags.Has(dto.Flags, MessageFlags.HasThread), Is.True);
    }

    [Test]
    public void ThreadIdIsProjected()
    {
        var message = MakeMessage();
        message.ThreadId = "chan-thread";

        var dto = message.ToFacet<Message, MessageDto>();

        Assert.That(dto.ThreadId, Is.EqualTo("chan-thread"));
    }
}
