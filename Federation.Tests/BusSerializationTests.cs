using System.Text.Json;
using System.Text.Json.Serialization;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;

namespace Federation.Tests;

/// <summary>
/// Federation must be able to deserialize every cross-service message it handles.
///
/// <para>This exists because it could not. <c>Program.cs</c> configured Wolverine's serializer with
/// a <c>TypeInfoResolverChain</c> containing only two source-generated contexts and no reflection
/// fallback, so any message type not explicitly annotated in one of them failed in
/// <c>HandlerPipeline.TryDeserializeEnvelope</c> - before the handler ran. Both GDPR lifecycle
/// commands were missing:</para>
/// <list type="bullet">
/// <item><c>ExportUserDataCommand</c> - every data export resolved <c>Partial</c> naming federation.</item>
/// <item><c>PurgeUserDataCommand</c> - every account deletion hung, because the deletion saga
/// deliberately never self-completes at its deadline.</item>
/// </list>
///
/// <para>The failure surfaced nowhere useful: not a compile error, not a startup error, no routing
/// warning. The envelope was consumed, discarded, and moved to a dead-letter queue nothing reads.
/// It took production log capture to find, which is exactly the kind of bug a test should own.</para>
///
/// <para>These assert against the serializer <b>as Program.cs configures it</b>. Reintroducing a
/// restrictive resolver chain fails here rather than in production.</para>
/// </summary>
[TestFixture]
public class BusSerializationTests
{
    /// <summary>
    /// The <b>production</b> configurator, not a copy of it. <c>Program.cs</c> passes this same
    /// method to <c>UseSystemTextJsonForSerialization</c>, so narrowing the resolver chain there
    /// fails these tests. An earlier draft rebuilt the options literally here, which would have let
    /// exactly the original bug back in without turning anything red.
    /// </summary>
    private static JsonSerializerOptions BusOptions()
    {
        var options = new JsonSerializerOptions();
        Federation.Application.FederationBusSerialization.Configure(options);
        return options;
    }

    private static IEnumerable<TestCaseData> CrossServiceMessages()
    {
        yield return new TestCaseData(
            new ExportUserDataCommand { ExportId = "dxrq_test", UserId = "user_test" })
            .SetName("ExportUserDataCommand");

        yield return new TestCaseData(
            new PurgeUserDataCommand { UserId = "user_test" })
            .SetName("PurgeUserDataCommand");

        yield return new TestCaseData(
            new IsUserAdministrativeRequest { UserId = "user_test" })
            .SetName("IsUserAdministrativeRequest");
    }

    [TestCaseSource(nameof(CrossServiceMessages))]
    public void EveryCrossServiceMessageFederationHandles_RoundTrips(object message)
    {
        var json = JsonSerializer.Serialize(message, message.GetType(), BusOptions());

        Assert.DoesNotThrow(
            () => JsonSerializer.Deserialize(json, message.GetType(), BusOptions()),
            $"{message.GetType().Name} could not be deserialized with Federation's bus serializer. "
            + "If a source-generated TypeInfoResolver has been reinstated as the whole chain, this "
            + "message type is now silently undeliverable: it will be consumed, dropped before the "
            + "handler, and dead-lettered with no error anywhere.");
    }

    /// <summary>
    /// The replies Federation sends back must survive the trip too - a fan-out participant that can
    /// receive but not answer is indistinguishable, from the saga's side, from one that is not
    /// deployed.
    /// </summary>
    [Test]
    public void TheRepliesFederationSends_RoundTrip()
    {
        var export = new ExportUserDataResponse
        {
            ExportId = "dxrq_test",
            UserId = "user_test",
            Service = "federation",
            FragmentJson = """{"notice":"nothing keyed directly to this account"}""",
            RowCounts = new Dictionary<string, int>(),
        };

        var purge = new PurgeUserDataCommandResponse { UserId = "user_test", Service = "federation" };

        Assert.Multiple(() =>
        {
            var exportJson = JsonSerializer.Serialize(export, BusOptions());
            var exportBack = JsonSerializer.Deserialize<ExportUserDataResponse>(exportJson, BusOptions());
            Assert.That(exportBack, Is.Not.Null);
            Assert.That(exportBack!.Service, Is.EqualTo("federation"));
            Assert.That(exportBack.FragmentJson, Is.EqualTo(export.FragmentJson));

            var purgeJson = JsonSerializer.Serialize(purge, BusOptions());
            var purgeBack = JsonSerializer.Deserialize<PurgeUserDataCommandResponse>(purgeJson, BusOptions());
            Assert.That(purgeBack, Is.Not.Null);
            Assert.That(purgeBack!.Service, Is.EqualTo("federation"));
        });
    }

    /// <summary>
    /// Pins the specific configuration mistake, so the test is about the mechanism and not just
    /// about the two types that happened to be missing this time. A chain of source-generated
    /// contexts with no reflection fallback rejects anything not annotated - which is what made the
    /// production failure silent and total rather than partial.
    /// </summary>
    [Test]
    public void ARestrictiveResolverChain_IsWhatBrokeThis_AndStillWould()
    {
        var restrictive = new JsonSerializerOptions();
        restrictive.Converters.Add(new JsonStringEnumConverter());
        restrictive.TypeInfoResolverChain.Insert(
            0, Federation.Application.Messages.FederationMessageContext.Default);

        var command = new ExportUserDataCommand { ExportId = "dxrq_test", UserId = "user_test" };

        Assert.Throws<NotSupportedException>(
            () => JsonSerializer.Serialize(command, restrictive),
            "if this stops throwing, the context now covers ExportUserDataCommand and the shape of "
            + "this test needs revisiting - but the hazard it documents is that an unannotated type "
            + "is rejected outright, not that this particular type is missing");
    }
}
