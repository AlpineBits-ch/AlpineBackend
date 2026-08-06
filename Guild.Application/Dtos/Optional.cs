using System.Text.Json;
using System.Text.Json.Serialization;

namespace Guild.Application.Dtos;

/// <summary>
/// A field that can tell "the caller did not send this" apart from "the caller sent null".
/// </summary>
[JsonConverter(typeof(OptionalConverterFactory))]
public readonly struct Optional<T>
{
    /// <summary>True when the property was present in the request body, null or not.</summary>
    public bool HasValue { get; }

    /// <summary>The value sent, which may itself be null.</summary>
    public T? Value { get; }

    private Optional(T? value)
    {
        HasValue = true;
        Value = value;
    }

    public static Optional<T> Of(T? value) => new(value);

    /// <summary>The value if it was sent, otherwise <paramref name="fallback"/> - the usual way to
    /// express "leave what is already there alone".</summary>
    public T? Or(T? fallback) => HasValue ? Value : fallback;
}

public class OptionalConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(OptionalConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

public class OptionalConverter<T> : JsonConverter<Optional<T>>
{
    /// <summary>The whole point.</summary>
    public override bool HandleNull => true;

    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null
            ? Optional<T>.Of(default)
            : Optional<T>.Of(JsonSerializer.Deserialize<T>(ref reader, options));

    /// <summary>Absent and explicitly-null both write null.</summary>
    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        if (!value.HasValue || value.Value is null) writer.WriteNullValue();
        else JsonSerializer.Serialize(writer, value.Value, options);
    }
}
