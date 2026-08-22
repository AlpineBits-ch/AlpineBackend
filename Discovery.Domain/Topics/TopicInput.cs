namespace Discovery.Domain.Topics;

/// <summary>A parsed topic plus the text the user actually typed, which the slug discards.</summary>
public readonly record struct TopicInput(TopicRef Topic, string? RawText);
