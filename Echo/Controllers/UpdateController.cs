using AppEnvironment;
using Microsoft.AspNetCore.Mvc;
using Octokit;

namespace Echo.Controllers;

[ApiController]
[Route("api/v1/update")]
public class UpdateController(IGitHubClient client, ILogger<UpdateController> logger, IHttpClientFactory httpClientFactory) : ControllerBase
{
    private const string Owner = "AlpineBits-ch";
    private const string Repo = "AlpineFrontend";

    private readonly string GitHubToken = Env.PersonalAccessToken;

    // Tauri v2 platform keys: windows-x86_64-msi = MSI, windows-x86_64 = NSIS (.exe)
    private static readonly (string Platform, string Suffix, string SigSuffix)[] PlatformAssets =
    [
        ("windows-x86_64-msi", "_en-US.msi",        "_en-US.msi.sig"),
        ("windows-x86_64",     "_x64-setup.exe",    "_x64-setup.exe.sig"),
        ("linux-x86_64",       "_amd64.deb",        "_amd64.deb.sig"),
        ("linux-x86_64-rpm",   ".x86_64.rpm",       ".x86_64.rpm.sig"),
    ];

    // How far back we walk when the newest releases have no (or incomplete) assets yet.
    private const int MaxReleasesToScan = 20;

    private record PlatformEntry(string signature, string url);

    [HttpGet("check/{currentVersion}")]
    public async Task<IActionResult> CheckUpdate(string currentVersion)
    {
        // A release that is still being built/uploaded on GitHub has no assets yet, so ladder
        // down to the newest release that actually carries a usable platform asset + signature.
        var latest = await FindNewestUsableRelease(HasAnyCompletePlatformAsset);
        if (latest == null)
            return NotFound("No release with supported platform assets found.");

        string latestVersion = latest.TagName.TrimStart('v');
        if (latestVersion == currentVersion.TrimStart('v'))
            return NoContent();

        var signatureTasks = PlatformAssets.Select(async p =>
        {
            var asset    = latest.Assets.FirstOrDefault(a => a.Name.EndsWith(p.Suffix));
            var sigAsset = latest.Assets.FirstOrDefault(a => a.Name.EndsWith(p.SigSuffix));
            if (asset == null || sigAsset == null)
                return (Platform: p.Platform, Sig: (string?)null);

            var sigResp = await FetchGitHubAsset(sigAsset.Url, "application/octet-stream");
            var sig = (await sigResp.Content.ReadAsStringAsync()).Trim();
            return (Platform: p.Platform, Sig: (string?)sig);
        });

        var signatures = await Task.WhenAll(signatureTasks);

        // Pin the download to the resolved version so the binary always matches the signature
        // handed out above, even if a newer (still empty) release shows up in the meantime.
        var baseUrl = $"https://api.venta.gg/api/v1/update/download/{latest.TagName}";
        var platforms = signatures
            .Where(s => s.Sig != null)
            .ToDictionary(
                s => s.Platform,
                s => new PlatformEntry(s.Sig!, $"{baseUrl}/{s.Platform}")
            );

        if (platforms.Count == 0)
            return NotFound("No supported platform assets found in this release.");

        return Ok(new
        {
            version  = latestVersion,
            notes    = latest.Body ?? "",
            pub_date = latest.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            platforms
        });
    }

    [HttpGet("download/latest/{platform}")]
    public async Task<IActionResult> DownloadLatest(string platform)
    {
        var entry = PlatformAssets.FirstOrDefault(p => p.Platform == platform);
        if (entry == default)
            return BadRequest($"Unsupported platform: {platform}");

        // Same laddering as the check endpoint: skip releases that do not carry this asset yet.
        var release = await FindNewestUsableRelease(r => r.Assets.Any(a => a.Name.EndsWith(entry.Suffix)));
        if (release == null)
        {
            logger.LogInformation("Asset not found in any recent release for platform: {platform}", platform);
            return NotFound();
        }

        return await StreamAsset(release, entry);
    }

    [HttpGet("download/{version}/{platform}")]
    public async Task<IActionResult> DownloadVersion(string version, string platform)
    {
        var entry = PlatformAssets.FirstOrDefault(p => p.Platform == platform);
        if (entry == default)
            return BadRequest($"Unsupported platform: {platform}");

        var releases = await GetRecentReleases();
        var release = releases.FirstOrDefault(r =>
            string.Equals(r.TagName.TrimStart('v'), version.TrimStart('v'), StringComparison.OrdinalIgnoreCase));

        if (release == null)
        {
            logger.LogInformation("Release not found: {version}", version);
            return NotFound();
        }

        return await StreamAsset(release, entry);
    }

    private async Task<IActionResult> StreamAsset(Release release, (string Platform, string Suffix, string SigSuffix) entry)
    {
        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(entry.Suffix));
        if (asset == null)
        {
            logger.LogInformation("Asset not found for platform {platform} in release {tag}", entry.Platform, release.TagName);
            return NotFound();
        }

        var assetResponse = await FetchGitHubAsset(asset.Url, "application/octet-stream");
        var bytes = await assetResponse.Content.ReadAsByteArrayAsync();
        return File(bytes, "application/octet-stream", asset.Name);
    }

    /// <summary>
    /// Walks the published releases from newest to oldest and returns the first one satisfying
    /// <paramref name="isUsable"/>. Releases that are still brewing (no assets uploaded yet) are
    /// skipped instead of failing the request.
    /// </summary>
    private async Task<Release?> FindNewestUsableRelease(Func<Release, bool> isUsable)
    {
        var releases = await GetRecentReleases();

        foreach (var release in releases)
        {
            if (isUsable(release))
                return release;

            logger.LogInformation("Skipping release {tag}: no usable assets yet.", release.TagName);
        }

        return null;
    }

    private async Task<IReadOnlyList<Release>> GetRecentReleases()
    {
        var options = new ApiOptions { PageSize = MaxReleasesToScan, PageCount = 1 };
        var releases = await client.Repository.Release.GetAll(Owner, Repo, options);

        return releases
            .Where(r => r is { Draft: false, Prerelease: false })
            .OrderByDescending(r => r.PublishedAt ?? r.CreatedAt)
            .ToList();
    }

    private static bool HasAnyCompletePlatformAsset(Release release) =>
        PlatformAssets.Any(p =>
            release.Assets.Any(a => a.Name.EndsWith(p.Suffix)) &&
            release.Assets.Any(a => a.Name.EndsWith(p.SigSuffix)));

    private async Task<HttpResponseMessage> FetchGitHubAsset(string assetUrl, string acceptHeader)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var tempClient = new HttpClient(handler);
        tempClient.DefaultRequestHeaders.Add("User-Agent", "TauriUpdater");
        tempClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {GitHubToken}");
        tempClient.DefaultRequestHeaders.Accept.ParseAdd(acceptHeader);

        var redirectResponse = await tempClient.GetAsync(assetUrl);

        if (redirectResponse.StatusCode != System.Net.HttpStatusCode.Redirect &&
            redirectResponse.StatusCode != System.Net.HttpStatusCode.Found)
        {
            return redirectResponse;
        }

        var cdnUrl = redirectResponse.Headers.Location
                     ?? throw new InvalidOperationException("No redirect Location header found.");

        var http = httpClientFactory.CreateClient();
        return await http.GetAsync(cdnUrl);
    }
}
