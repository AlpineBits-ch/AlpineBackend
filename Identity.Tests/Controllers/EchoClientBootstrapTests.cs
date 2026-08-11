using Identity.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Identity.Tests.Controllers;

/// <summary>Pins the backfill of the first-party <c>echo</c> client.</summary>
[TestFixture]
public class EchoClientBootstrapTests
{
    private IServiceScope scope = null!;
    private IOpenIddictApplicationManager manager = null!;

    private static readonly string SteamGrant =
        OpenIddictConstants.Permissions.Prefixes.GrantType
        + Application.Services.Steam.SteamOpenIdService.SteamGrantType;

    private static readonly string QrGrant =
        OpenIddictConstants.Permissions.Prefixes.GrantType
        + Application.Services.Qr.QrLoginService.QrGrantType;

    [SetUp]
    public void SetUp()
    {
        scope = AppFixture.Host.Services.CreateScope();
        manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
    }

    /// <summary>
    /// Every test here mutates a row the whole fixture shares, so it is put back before the scope
    /// goes away.
    /// </summary>
    [TearDown]
    public async Task TearDownAsync()
    {
        await EchoClientBootstrap.EnsureAsync(manager);
        scope.Dispose();
    }

    private async Task<HashSet<string>> PermissionsAsync()
    {
        var application = await manager.FindByClientIdAsync(EchoClientBootstrap.ClientId);
        Assert.That(application, Is.Not.Null, "the echo client should exist after startup");

        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, application!);

        return [.. descriptor.Permissions];
    }

    private async Task SetPermissionsAsync(IEnumerable<string> permissions)
    {
        var application = await manager.FindByClientIdAsync(EchoClientBootstrap.ClientId);
        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, application!);

        descriptor.Permissions.Clear();
        descriptor.Permissions.UnionWith(permissions);

        await manager.UpdateAsync(application!, descriptor);
    }

    [Test]
    public async Task Startup_leaves_the_echo_client_holding_every_declared_permission()
    {
        Assert.That(await PermissionsAsync(), Is.SupersetOf(EchoClientBootstrap.Permissions));
    }

    /// <summary>The regression.</summary>
    [Test]
    public async Task An_existing_client_missing_the_steam_grant_has_it_backfilled()
    {
        var original = await PermissionsAsync();
        await SetPermissionsAsync(original.Where(permission => permission != SteamGrant));

        Assert.That(await PermissionsAsync(), Does.Not.Contain(SteamGrant), "arrange failed");

        var changed = await EchoClientBootstrap.EnsureAsync(manager);
        var permissions = await PermissionsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True, "a stale row is a change");
            Assert.That(permissions, Contains.Item(SteamGrant));
        });
    }

    [Test]
    public async Task An_existing_client_missing_every_grant_gets_all_of_them_back()
    {
        await SetPermissionsAsync([OpenIddictConstants.Permissions.Endpoints.Token]);

        await EchoClientBootstrap.EnsureAsync(manager);

        var permissions = await PermissionsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(permissions, Contains.Item(SteamGrant));
            Assert.That(permissions, Contains.Item(QrGrant));
            Assert.That(permissions, Is.SupersetOf(EchoClientBootstrap.Permissions));
        });
    }

    /// <summary>
    /// The other half of "union, never subtract": a permission granted out of band survives.
    /// </summary>
    [Test]
    public async Task A_permission_granted_out_of_band_is_not_revoked()
    {
        var outOfBand = OpenIddictConstants.Permissions.Endpoints.Revocation;

        await SetPermissionsAsync([.. EchoClientBootstrap.Permissions, outOfBand]);

        var changed = await EchoClientBootstrap.EnsureAsync(manager);
        var permissions = await PermissionsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False, "nothing was missing, so nothing should be written");
            Assert.That(permissions, Contains.Item(outOfBand));
        });
    }

    [Test]
    public async Task Ensure_is_idempotent()
    {
        await EchoClientBootstrap.EnsureAsync(manager);

        Assert.That(await EchoClientBootstrap.EnsureAsync(manager), Is.False);
    }
}
