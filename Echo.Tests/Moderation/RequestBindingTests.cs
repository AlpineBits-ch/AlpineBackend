using System.Text.Json;
using System.Text.Json.Serialization;
using Echo.Domain.Enums;
using Echo.Moderation;

namespace Echo.Tests.Moderation;

/// <summary>The request bodies the hand-written pages actually send.</summary>
[TestFixture]
[Category("Unit")]
public class RequestBindingTests
{
    /// <summary>Mirrors the converter registered on <c>AddControllers</c> in Echo/Program.cs.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Verbatim from the console's ban dialog, including the null duration that makes a
    /// ban indefinite.</summary>
    [Test]
    public void The_consoles_ban_payload_binds()
    {
        const string body = """
            {"kind":"Ban","reason":"ViolentThreats",
             "publicNote":"note to the user","internalNote":"note to staff",
             "durationHours":null,"notify":true}
            """;

        var request = JsonSerializer.Deserialize<IssueActionRequest>(body, Options);

        Assert.That(request, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(request!.Kind, Is.EqualTo(ModerationActionKind.Ban));
            Assert.That(request.Reason, Is.EqualTo(ReportReason.ViolentThreats));
            Assert.That(request.DurationHours, Is.Null, "a null duration is an indefinite ban");
            Assert.That(request.Notify, Is.True);
        });
    }

    /// <summary>Verbatim from the support site's contact form.</summary>
    [Test]
    public void The_support_forms_ticket_payload_binds()
    {
        const string body = """
            {"email":"person@example.com","subject":"a subject",
             "category":"Safety","body":"what happened"}
            """;

        var request = JsonSerializer.Deserialize<OpenTicketRequest>(body, Options);

        Assert.That(request, Is.Not.Null);
        Assert.That(request!.Category, Is.EqualTo(SupportTicketCategory.Safety));
    }

    [Test]
    public void The_report_payload_binds()
    {
        const string body = """
            {"targetUserId":"user_abc","subjectKind":"Message","subjectId":"mesg_abc",
             "reason":"Harassment","details":"why","evidence":{"encrypted":false,"messages":[]}}
            """;

        var request = JsonSerializer.Deserialize<SubmitReportRequest>(body, Options);

        Assert.That(request, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(request!.SubjectKind, Is.EqualTo(ReportSubjectKind.Message));
            Assert.That(request.Reason, Is.EqualTo(ReportReason.Harassment));
            Assert.That(request.Evidence, Is.Not.Null, "the snapshot is passed through as-is");
        });
    }

    /// <summary>Every enum name in every request DTO binds, not just the ones a page happens to send
    /// today. A value added to an enum is unreachable the moment a page offers it otherwise.</summary>
    [Test]
    public void Every_enum_member_binds_by_name()
    {
        Assert.Multiple(() =>
        {
            foreach (var name in Enum.GetNames<ModerationActionKind>())
            {
                var request = JsonSerializer.Deserialize<IssueActionRequest>(
                    $$"""{"kind":"{{name}}","reason":"Other"}""", Options);

                Assert.That(request!.Kind.ToString(), Is.EqualTo(name));
            }

            foreach (var name in Enum.GetNames<ReportReason>())
            {
                var request = JsonSerializer.Deserialize<SubmitReportRequest>(
                    $$"""{"targetUserId":"user_a","subjectKind":"User","reason":"{{name}}"}""", Options);

                Assert.That(request!.Reason.ToString(), Is.EqualTo(name));
            }

            foreach (var name in Enum.GetNames<SupportTicketCategory>())
            {
                var request = JsonSerializer.Deserialize<OpenTicketRequest>(
                    $$"""{"email":"a@b.co","subject":"s","category":"{{name}}","body":"b"}""", Options);

                Assert.That(request!.Category.ToString(), Is.EqualTo(name));
            }
        });
    }

    /// <summary>The gateway is configured the way the tests above assume.</summary>
    [Test]
    public void The_gateway_registers_the_string_enum_converter()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Echo", "Program.cs")))
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "could not locate Echo/Program.cs");

        var source = File.ReadAllText(Path.Combine(directory!.FullName, "Echo", "Program.cs"));

        Assert.That(source, Does.Contain("JsonStringEnumConverter"),
            "without it every enum in a request body expects an integer, and every page that posts "
            + "one gets a 400 naming the enum type");
    }
}
