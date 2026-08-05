using Echo.Moderation;
using Echo.Persistence.Persistance;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Echo.Tests.Moderation;

/// <summary>Telling "you are not staff" apart from "we could not check".</summary>
[TestFixture]
[Category("Unit")]
public class StaffRefusalTests
{
    /// <summary>Exposes the protected refusal.</summary>
    private sealed class Probe : AdminControllerBase
    {
        public Probe(HttpContext context) : base(null!, null!) =>
            ControllerContext = new ControllerContext { HttpContext = context };

        public IActionResult Refuse() => StaffForbidden();
    }

    private static (int Status, string Code) Refuse(bool unavailable)
    {
        var context = new DefaultHttpContext();
        if (unavailable) context.Items[StaffAccess.UnavailableItemKey] = true;

        var result = (ObjectResult)new Probe(context).Refuse();
        var code = result.Value!.GetType().GetProperty("code")!.GetValue(result.Value)!;

        return (result.StatusCode!.Value, (string)code);
    }

    [Test]
    public void A_non_staff_account_is_refused_as_forbidden()
    {
        var (status, code) = Refuse(unavailable: false);

        Assert.That(status, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(code, Is.EqualTo("staff_required"));
    }

    /// <summary>The regression.</summary>
    [Test]
    public void A_check_that_could_not_be_completed_is_refused_as_unavailable()
    {
        var (status, code) = Refuse(unavailable: true);

        Assert.That(status, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
        Assert.That(code, Is.EqualTo("staff_check_unavailable"));
    }

    /// <summary>Still denied.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public void Either_way_the_request_is_denied(bool unavailable)
    {
        var (status, _) = Refuse(unavailable);

        Assert.That(status, Is.GreaterThanOrEqualTo(400));
    }
}
