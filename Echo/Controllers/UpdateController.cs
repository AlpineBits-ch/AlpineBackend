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

    private record PlatformEntry(string signature, string url);

    [HttpGet("check/{currentVersion}")]
    public async Task<IActionResult> CheckUpdate(string currentVersion)
    {
        var latest = await client.Repository.Release.GetLatest(Owner, Repo);

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

        var baseUrl = $"https://api.alpinebits.ch/api/v1/update/download/latest";
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

        var latest = await client.Repository.Release.GetLatest(Owner, Repo);
        var asset = latest.Assets.FirstOrDefault(a => a.Name.EndsWith(entry.Suffix));
        if (asset == null)
        {
            logger.LogInformation("Asset not found for platform: {platform}", platform);
            return NotFound();
        }

        var assetResponse = await FetchGitHubAsset(asset.Url, "application/octet-stream");
        var bytes = await assetResponse.Content.ReadAsByteArrayAsync();
        return File(bytes, "application/octet-stream", asset.Name);
    }

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
