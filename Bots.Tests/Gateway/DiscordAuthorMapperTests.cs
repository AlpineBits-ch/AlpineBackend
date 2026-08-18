using System.Text.Json;
using Bots.Application.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Guild.Contracts.Bus.Events;

namespace Bots.Tests.Gateway;

/// <summary>
/// Pins the Discord-compat author mapping: a persona keeps the real account in author.id while
/// wearing the character everywhere a client renders, a real webhook keeps Discord's own shape, and
/// an ordinary user message is untouched.
/// </summary>
[TestFixture]
public class DiscordAuthorMapperTests
{
    private static MessageCreatePayload Map(DiscordAuthorSource source)
    {
        var payload = new MessageCreatePayload { Id = "msg_1", ChannelId = "ch_1", Content = "hi" };
        DiscordAuthorMapper.Apply(payload, source);
        return payload;
    }

    private static JsonElement Serialize(MessageCreatePayload payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;

    private static DiscordAuthorSource Persona(string? personaId = "pers_cogsgrove") => new(
        AuthorIdType.Persona, "usr_dominic", "Mayor Cogsgrove", "https://cdn.venta.gg/p/cogsgrove.png",
        personaId, "dominic", false);

    private static DiscordAuthorSource Webhook() => new(
        AuthorIdType.Webhook, "whk_alerts", "Build Bot", "https://cdn.venta.gg/w/alerts.png",
        null, null, false);

    private static DiscordAuthorSource User() => new(
        AuthorIdType.User, "usr_dominic", null, null, null, "dominic", false);

    [Test]
    public void Persona_KeepsTheRealAccountInAuthorId()
    {
        var payload = Map(Persona());

        Assert.Multiple(() =>
        {
            Assert.That(payload.Author.Id, Is.EqualTo("usr_dominic"));
            Assert.That(payload.Author.Username, Is.EqualTo("Mayor Cogsgrove"));
            Assert.That(payload.Author.Avatar, Is.EqualTo("https://cdn.venta.gg/p/cogsgrove.png"));
            Assert.That(payload.Author.Bot, Is.False);
        });
    }

    [Test]
    public void Persona_SetsWebhookIdToThePersonaSoItIsNeverEqualToTheAuthorId()
    {
        var payload = Map(Persona());

        Assert.Multiple(() =>
        {
            Assert.That(payload.WebhookId, Is.EqualTo("pers_cogsgrove"));
            Assert.That(payload.WebhookId, Is.Not.EqualTo(payload.Author.Id));
            Assert.That(payload.AuthorType, Is.EqualTo("persona"));
            Assert.That(payload.Persona!.Id, Is.EqualTo("pers_cogsgrove"));
            Assert.That(payload.Persona.Name, Is.EqualTo("Mayor Cogsgrove"));
            Assert.That(payload.Persona.AvatarUrl, Is.EqualTo("https://cdn.venta.gg/p/cogsgrove.png"));
        });
    }

    [Test]
    public void Persona_SerializesTheAdditiveFieldsAlongsideDiscordsOwn()
    {
        var data = Serialize(Map(Persona()));

        Assert.Multiple(() =>
        {
            Assert.That(data.GetProperty("webhook_id").GetString(), Is.EqualTo("pers_cogsgrove"));
            Assert.That(data.GetProperty("author_type").GetString(), Is.EqualTo("persona"));
            Assert.That(data.GetProperty("author").GetProperty("id").GetString(), Is.EqualTo("usr_dominic"));
            Assert.That(data.GetProperty("persona").GetProperty("id").GetString(), Is.EqualTo("pers_cogsgrove"));
            Assert.That(data.GetProperty("persona").GetProperty("avatar_url").GetString(),
                Is.EqualTo("https://cdn.venta.gg/p/cogsgrove.png"));
        });
    }

    /// <summary>A character arriving over federation is display data with no local persona row.</summary>
    [Test]
    public void FederatedPersona_StillDeclaresItselfWithNoPersonaId()
    {
        var payload = Map(Persona(personaId: null));

        Assert.Multiple(() =>
        {
            Assert.That(payload.AuthorType, Is.EqualTo("persona"));
            Assert.That(payload.Persona!.Id, Is.Null);
            // Only so the costume stays out of discord.js's user cache - author_type is the answer.
            Assert.That(payload.WebhookId, Is.EqualTo("usr_dominic"));
        });
    }

    [Test]
    public void Webhook_KeepsDiscordsShapeWithAuthorIdEqualToWebhookId()
    {
        var payload = Map(Webhook());

        Assert.Multiple(() =>
        {
            Assert.That(payload.Author.Id, Is.EqualTo("whk_alerts"));
            Assert.That(payload.Author.Username, Is.EqualTo("Build Bot"));
            Assert.That(payload.Author.Bot, Is.True);
            Assert.That(payload.WebhookId, Is.EqualTo("whk_alerts"));
            Assert.That(payload.AuthorType, Is.EqualTo("webhook"));
            Assert.That(payload.Persona, Is.Null);
        });
    }

    [Test]
    public void Webhook_OmitsThePersonaBlockFromTheWire()
    {
        var data = Serialize(Map(Webhook()));

        Assert.Multiple(() =>
        {
            Assert.That(data.TryGetProperty("persona", out _), Is.False);
            Assert.That(data.GetProperty("webhook_id").GetString(), Is.EqualTo("whk_alerts"));
        });
    }

    [Test]
    public void OrdinaryUser_IsUnchangedAndCarriesNoWebhookId()
    {
        var payload = Map(User());
        var data = Serialize(payload);

        Assert.Multiple(() =>
        {
            Assert.That(payload.Author.Id, Is.EqualTo("usr_dominic"));
            Assert.That(payload.Author.Username, Is.EqualTo("dominic"));
            Assert.That(payload.Author.Avatar, Is.Null);
            Assert.That(payload.Author.Bot, Is.False);
            Assert.That(payload.AuthorType, Is.EqualTo("user"));
            Assert.That(data.TryGetProperty("webhook_id", out _), Is.False);
            Assert.That(data.TryGetProperty("persona", out _), Is.False);
        });
    }

    [Test]
    public void UnknownAccount_FallsBackToTheAuthorIdAsUsername()
    {
        var payload = Map(new DiscordAuthorSource(
            AuthorIdType.User, "usr_ghost", null, null, null, null, false));

        Assert.That(payload.Author.Username, Is.EqualTo("usr_ghost"));
    }

    [Test]
    public void BotAccount_IsMarkedAsABotWithoutAWebhookId()
    {
        var payload = Map(new DiscordAuthorSource(
            AuthorIdType.Bot, "usr_bot1", null, null, null, "Test Bot", true));

        Assert.Multiple(() =>
        {
            Assert.That(payload.Author.Bot, Is.True);
            Assert.That(payload.AuthorType, Is.EqualTo("bot"));
            Assert.That(payload.WebhookId, Is.Null);
        });
    }
}
