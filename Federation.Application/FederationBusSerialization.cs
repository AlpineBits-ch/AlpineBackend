using System.Text.Json;
using System.Text.Json.Serialization;

namespace Federation.Application;

/// <summary>
/// How Federation serializes messages on the bus.
///
/// <para>Extracted from <c>Program.cs</c> so it can be asserted on. It previously lived inline as a
/// lambda, which meant nothing could test it, and what it did wrong was invisible until production:
/// it installed two source-generated contexts as the <b>entire</b> <c>TypeInfoResolverChain</c>,
/// with no reflection fallback. Any message type not annotated in one of them failed in
/// <c>HandlerPipeline.TryDeserializeEnvelope</c> before the handler ran - the envelope was consumed,
/// dropped, and dead-lettered, with no compile error, no startup error and no routing warning.</para>
///
/// <para>Both cross-service GDPR commands were missing, so Federation answered neither: exports
/// resolved <c>Partial</c> naming it, and account deletions hung because the deletion saga never
/// self-completes at its deadline.</para>
///
/// <para>Reflection-based resolution now, matching the other seven services. Nothing here needs
/// source generation - Federation is neither trimmed nor AOT-published. <c>BusSerializationTests</c>
/// calls this method directly, so narrowing the resolver again fails a test instead of a GDPR
/// request.</para>
/// </summary>
public static class FederationBusSerialization
{
    public static void Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
    }
}
