using System.Text.Json;
using System.Text.Json.Serialization;

namespace Federation.Application;

/// <summary>How Federation serializes messages on the bus.</summary>
public static class FederationBusSerialization
{
    public static void Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
    }
}
