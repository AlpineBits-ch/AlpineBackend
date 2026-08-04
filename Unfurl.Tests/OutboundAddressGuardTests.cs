using System.Net;
using AppEnvironment.Security;

namespace Unfurl.Tests;

/// <summary>
/// The SSRF guard. Anyone who can type a URL into a chat box chooses what the unfurler dials, so
/// every case here is an attack someone would actually try.
/// </summary>
public class OutboundAddressGuardTests
{
    // ── Blocked address ranges ───────────────────────────────────────────────

    [TestCase("127.0.0.1", Description = "loopback")]
    [TestCase("127.1.2.3", Description = "the rest of 127/8, which people forget")]
    [TestCase("0.0.0.0")]
    [TestCase("10.1.2.3", Description = "RFC1918")]
    [TestCase("172.16.0.1", Description = "RFC1918 lower bound")]
    [TestCase("172.31.255.254", Description = "RFC1918 upper bound")]
    [TestCase("192.168.1.1", Description = "RFC1918")]
    [TestCase("169.254.169.254", Description = "cloud metadata - the single most valuable SSRF target")]
    [TestCase("100.64.0.1", Description = "CGNAT")]
    [TestCase("224.0.0.1", Description = "multicast")]
    [TestCase("255.255.255.255")]
    public void IsBlocked_NonRoutableIpv4_IsBlocked(string address)
    {
        Assert.That(OutboundAddressGuard.IsBlocked(IPAddress.Parse(address)), Is.True);
    }

    [TestCase("::1", Description = "IPv6 loopback")]
    [TestCase("fe80::1", Description = "link-local")]
    [TestCase("fc00::1", Description = "unique local")]
    [TestCase("fd12:3456::1", Description = "unique local, the half people actually use")]
    [TestCase("::")]
    public void IsBlocked_NonRoutableIpv6_IsBlocked(string address)
    {
        Assert.That(OutboundAddressGuard.IsBlocked(IPAddress.Parse(address)), Is.True);
    }

    [Test]
    public void IsBlocked_Ipv4MappedIpv6_UnwrapsBeforeDeciding()
    {
        // ::ffff:169.254.169.254 is the metadata endpoint wearing an IPv6 costume. Without the
        // unwrap it walks straight past every IPv4 rule above.
        Assert.Multiple(() =>
        {
            Assert.That(OutboundAddressGuard.IsBlocked(IPAddress.Parse("::ffff:169.254.169.254")), Is.True);
            Assert.That(OutboundAddressGuard.IsBlocked(IPAddress.Parse("::ffff:127.0.0.1")), Is.True);
            Assert.That(OutboundAddressGuard.IsBlocked(IPAddress.Parse("::ffff:10.0.0.1")), Is.True);
        });
    }

    [TestCase("1.1.1.1")]
    [TestCase("93.184.216.34")]
    [TestCase("2606:4700:4700::1111")]
    public void IsBlocked_PublicAddresses_AreAllowed(string address)
    {
        Assert.That(OutboundAddressGuard.IsBlocked(IPAddress.Parse(address)), Is.False);
    }

    // ── URL validation ───────────────────────────────────────────────────────

    [Test]
    public void TryValidate_OrdinaryHttpsUrl_IsAccepted()
    {
        var ok = OutboundAddressGuard.TryValidate("https://example.com/page", false, out var uri, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(uri!.Host, Is.EqualTo("example.com"));
            Assert.That(error, Is.Null);
        });
    }

    [TestCase("javascript:alert(1)")]
    [TestCase("file:///etc/passwd")]
    [TestCase("gopher://example.com/")]
    [TestCase("ftp://example.com/")]
    public void TryValidate_NonHttpScheme_IsRejected(string url)
    {
        Assert.That(OutboundAddressGuard.TryValidate(url, false, out _, out _), Is.False);
    }

    [Test]
    public void TryValidate_EmbeddedCredentials_AreRejected()
    {
        var ok = OutboundAddressGuard.TryValidate("https://user:pass@example.com/", false, out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("credentials"));
        });
    }

    [TestCase("http://127.0.0.1/admin")]
    [TestCase("http://169.254.169.254/latest/meta-data/")]
    [TestCase("http://[::1]/")]
    public void TryValidate_LiteralPrivateAddress_IsRejected(string url)
    {
        Assert.That(OutboundAddressGuard.TryValidate(url, false, out _, out _), Is.False);
    }

    [Test]
    public void TryValidate_LiteralPrivateAddress_IsAllowedWhenPrivateTargetsAre()
    {
        // The development/E2E escape hatch. Production never sets it - see UnfurlConfiguration.
        Assert.That(OutboundAddressGuard.TryValidate("http://127.0.0.1:5000/fixture", true, out _, out _), Is.True);
    }

    [Test]
    public void TryValidate_HostnameResolvingPrivately_PassesHereAndIsCaughtAtConnect()
    {
        // Documents the division of labour deliberately: a *name* cannot be judged up front,
        // because what it resolves to now is not what it will resolve to when the socket opens.
        // That is the DNS-rebinding case, and it is the connect callback's job, not this one's.
        var ok = OutboundAddressGuard.TryValidate("http://localhost.internal.example/", false, out _, out _);

        Assert.That(ok, Is.True, "hostname validation is deferred to the connect callback by design");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not a url")]
    [TestCase("/relative/path")]
    public void TryValidate_Garbage_IsRejected(string? url)
    {
        Assert.That(OutboundAddressGuard.TryValidate(url, false, out _, out _), Is.False);
    }
}
