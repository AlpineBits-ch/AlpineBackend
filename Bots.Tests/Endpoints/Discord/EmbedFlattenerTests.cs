using Bots.Application.Endpoints.Discord;
using Bots.Contracts.Gateway.Payloads;

namespace Bots.Tests.Endpoints.Discord;

[TestFixture]
public class EmbedFlattenerTests
{
    [Test]
    public void Flatten_ContentOnly_NoEmbeds_ReturnsContentUnchanged()
    {
        var result = EmbedFlattener.Flatten("hello world", null);

        Assert.That(result, Is.EqualTo("hello world"));
    }

    [Test]
    public void Flatten_NoContentNoEmbeds_ReturnsEmptyString()
    {
        var result = EmbedFlattener.Flatten(null, null);

        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Flatten_EmptyEmbedsList_ReturnsContentOnly()
    {
        var result = EmbedFlattener.Flatten("just content", new List<EmbedPayload>());

        Assert.That(result, Is.EqualTo("just content"));
    }

    [Test]
    public void Flatten_EmbedWithTitleAndDescription_IncludesBoth()
    {
        var embeds = new List<EmbedPayload>
        {
            new() { Title = "My Title", Description = "My Description" }
        };

        var result = EmbedFlattener.Flatten(null, embeds);

        Assert.That(result, Does.Contain("**My Title**"));
        Assert.That(result, Does.Contain("My Description"));
    }

    [Test]
    public void Flatten_EmbedWithAuthorTitleUrlDescription_IncludesAllInOrder()
    {
        var embeds = new List<EmbedPayload>
        {
            new()
            {
                Author = new EmbedAuthorPayload { Name = "Author Name" },
                Title = "Title",
                Url = "https://example.com",
                Description = "Description",
            }
        };

        var result = EmbedFlattener.Flatten(null, embeds);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.That(lines[0], Is.EqualTo("Author Name"));
        Assert.That(lines[1], Is.EqualTo("**Title**"));
        Assert.That(lines[2], Is.EqualTo("https://example.com"));
        Assert.That(lines[3], Is.EqualTo("Description"));
    }

    [Test]
    public void Flatten_EmbedWithFields_IncludesNameValuePairs()
    {
        var embeds = new List<EmbedPayload>
        {
            new()
            {
                Fields =
                [
                    new EmbedFieldPayload { Name = "Status", Value = "Online" },
                    new EmbedFieldPayload { Name = "Uptime", Value = "5 days" },
                ]
            }
        };

        var result = EmbedFlattener.Flatten(null, embeds);

        Assert.That(result, Does.Contain("Status: Online"));
        Assert.That(result, Does.Contain("Uptime: 5 days"));
    }

    [Test]
    public void Flatten_EmbedWithFooter_IncludesFooterText()
    {
        var embeds = new List<EmbedPayload>
        {
            new() { Footer = new EmbedFooterPayload { Text = "footer text" } }
        };

        var result = EmbedFlattener.Flatten(null, embeds);

        Assert.That(result, Does.Contain("footer text"));
    }

    [Test]
    public void Flatten_ContentPlusEmbed_ContentComesFirst()
    {
        var embeds = new List<EmbedPayload> { new() { Title = "Embed Title" } };

        var result = EmbedFlattener.Flatten("Message content", embeds);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.That(lines[0], Is.EqualTo("Message content"));
        Assert.That(lines[1], Is.EqualTo("**Embed Title**"));
    }

    [Test]
    public void Flatten_MultipleEmbeds_SeparatesWithBlankLine()
    {
        var embeds = new List<EmbedPayload>
        {
            new() { Title = "First" },
            new() { Title = "Second" },
        };

        var result = EmbedFlattener.Flatten(null, embeds);

        Assert.That(result, Does.Contain("**First**"));
        Assert.That(result, Does.Contain("**Second**"));
        // A blank line separates the two embed blocks.
        Assert.That(result.Replace("\r\n", "\n"), Does.Contain("**First**\n\n**Second**"));
    }

    [Test]
    public void Flatten_WhitespaceOnlyContent_IsTreatedAsNoContent()
    {
        var result = EmbedFlattener.Flatten("   ", null);

        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Flatten_EmbedWithNoFields_NoTrailingWhitespace()
    {
        var embeds = new List<EmbedPayload> { new() { Title = "Only Title" } };

        var result = EmbedFlattener.Flatten(null, embeds);

        Assert.That(result, Is.EqualTo(result.TrimEnd()), "Result must not have trailing whitespace");
    }
}
