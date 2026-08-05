using AppEnvironment;
using Echo.Controllers;

namespace Echo.Tests.Controllers;

/// <summary>The download URL the Tauri updater is handed by <c>/api/v1/update/check</c>.</summary>
[TestFixture]
public class UpdateDownloadUrlTests
{
    private string _originalInstanceUrl = null!;

    [SetUp]
    public void SetUp() => _originalInstanceUrl = Env.GeneralConfiguration.InstanceUrl;

    [TearDown]
    public void TearDown() => Env.GeneralConfiguration.InstanceUrl = _originalInstanceUrl;

    [Test]
    public void DownloadBaseUrl_UsesTheConfiguredInstanceUrl()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://chat.selfhosted.example";

        Assert.That(UpdateController.DownloadBaseUrl("v1.2.3"),
            Is.EqualTo("https://chat.selfhosted.example/api/v1/update/download/v1.2.3"));
    }

    [Test]
    public void DownloadBaseUrl_TrailingSlashOnInstanceUrl_DoesNotDoubleTheSeparator()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://chat.selfhosted.example/";

        Assert.That(UpdateController.DownloadBaseUrl("v1.2.3"),
            Is.EqualTo("https://chat.selfhosted.example/api/v1/update/download/v1.2.3"));
    }

    [Test]
    public void DownloadBaseUrl_DefaultInstanceUrl_IsTheHostProductionAlreadyServed()
    {
        // The INSTANCE_URL fallback is the exact string this replaced, so venta.gg's updater
        // responses are unchanged by the substitution even if the variable were never set.
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";

        Assert.That(UpdateController.DownloadBaseUrl("v1.2.3"),
            Is.EqualTo("https://api.venta.gg/api/v1/update/download/v1.2.3"));
    }
}
