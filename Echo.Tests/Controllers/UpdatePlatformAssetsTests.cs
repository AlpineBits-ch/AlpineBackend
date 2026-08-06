using Echo.Controllers;

namespace Echo.Tests.Controllers;

/// <summary>
/// Which platform keys <c>/api/v1/update/check</c> advertises to the Tauri updater.
/// </summary>
[TestFixture]
public class UpdatePlatformAssetsTests
{
    [Test]
    public void NoMsiPlatformIsAdvertised()
    {
        Assert.That(
            UpdateController.PlatformAssets.Select(p => p.Platform),
            Has.None.Contains("msi"),
            "An MSI platform key routes MSI-stamped clients onto a per-machine installer whose " +
            "silent update cannot elevate, so their updates fail without any error. Windows is " +
            "served by the NSIS 'windows-x86_64' key alone.");
    }

    [Test]
    public void NoMsiAssetSuffixIsServed()
    {
        // The suffixes drive DownloadLatest's backwards ladder through releases, so an .msi suffix
        // left here would serve the last release that still carried one under a "latest" URL.
        Assert.That(
            UpdateController.PlatformAssets.Select(p => p.Suffix),
            Has.None.EndsWith(".msi"));
    }

    [Test]
    public void WindowsIsServedByTheNsisSetupExe()
    {
        var windows = UpdateController.PlatformAssets.Single(p => p.Platform == "windows-x86_64");

        Assert.Multiple(() =>
        {
            Assert.That(windows.Suffix, Is.EqualTo("_x64-setup.exe"));
            Assert.That(windows.SigSuffix, Is.EqualTo("_x64-setup.exe.sig"));
        });
    }

    [Test]
    public void EveryPlatformHasASignatureSuffixForItsAsset()
    {
        // A platform whose signature suffix does not match its asset never produces a complete
        // pair, so CheckUpdate drops it from the response and that platform stops updating -
        // quietly, because the endpoint still answers 200 for the others.
        Assert.Multiple(() =>
        {
            foreach (var (platform, suffix, sigSuffix) in UpdateController.PlatformAssets)
            {
                Assert.That(sigSuffix, Is.EqualTo(suffix + ".sig"),
                    $"signature suffix for {platform} does not correspond to its asset suffix");
            }
        });
    }
}
