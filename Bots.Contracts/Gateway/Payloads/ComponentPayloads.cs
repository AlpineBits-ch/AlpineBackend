using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

/// <summary>Discord's component type numbers.</summary>
public static class ComponentType
{
    public const int ActionRow = 1;
    public const int Button = 2;
    public const int StringSelect = 3;
    public const int TextInput = 4;
    public const int UserSelect = 5;
    public const int RoleSelect = 6;
    public const int MentionableSelect = 7;
    public const int ChannelSelect = 8;
}

/// <summary>One entry in a message's `components` array.</summary>
public class ComponentPayload
{
    [JsonPropertyName("type")]
    public int Type { get; set; }

    /// <summary>Only set on ActionRow, which is the sole container type.</summary>
    [JsonPropertyName("components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ComponentPayload>? Components { get; set; }

    /// <summary>The bot-chosen identifier echoed back when a user interacts.</summary>
    [JsonPropertyName("custom_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomId { get; set; }

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    /// <summary>Button style 1-4 (primary/secondary/success/danger) or 5 (link).</summary>
    [JsonPropertyName("style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Style { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("emoji")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ComponentEmojiPayload? Emoji { get; set; }

    [JsonPropertyName("disabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Disabled { get; set; }

    [JsonPropertyName("placeholder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Placeholder { get; set; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ComponentSelectOptionPayload>? Options { get; set; }

    [JsonPropertyName("min_values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinValues { get; set; }

    [JsonPropertyName("max_values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxValues { get; set; }

    // ── Text input (modals only) ─────────────────────────────────────────────

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }

    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Required { get; set; }

    [JsonPropertyName("min_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinLength { get; set; }

    [JsonPropertyName("max_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; set; }

    /// <summary>Every custom_id reachable in this subtree, itself included.</summary>
    public IEnumerable<string> CollectCustomIds()
    {
        if (!string.IsNullOrWhiteSpace(CustomId)) yield return CustomId;

        if (Components is null) yield break;

        foreach (var child in Components)
        foreach (var id in child.CollectCustomIds())
        {
            yield return id;
        }
    }
}

public class ComponentEmojiPayload
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("animated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Animated { get; set; }
}

public class ComponentSelectOptionPayload
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("emoji")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ComponentEmojiPayload? Emoji { get; set; }

    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Default { get; set; }
}
