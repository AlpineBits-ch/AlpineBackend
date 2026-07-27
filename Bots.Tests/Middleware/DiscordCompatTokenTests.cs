using Bots.Application.Middleware;

namespace Bots.Tests.Middleware;

/// <summary>
/// Covers the pack/unpack round-trip used by the Discord-compat token shim: a bot's
/// compat token is base64(client_id:client_secret), unpacked by
/// DiscordBotTokenTranslationMiddleware before exchanging it for a real JWT.
/// </summary>
[TestFixture]
public class DiscordCompatTokenTests
{
    [Test]
    public void Pack_Unpack_RoundTrips()
    {
        var packed = DiscordCompatToken.Pack("user_abc123", "s3cr3t");

        var ok = DiscordCompatToken.TryUnpack(packed, out var clientId, out var clientSecret);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(clientId, Is.EqualTo("user_abc123"));
            Assert.That(clientSecret, Is.EqualTo("s3cr3t"));
        });
    }

    [Test]
    public void Pack_ProducesBase64OfColonJoinedCredentials()
    {
        var packed = DiscordCompatToken.Pack("user_1", "secret");

        var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("user_1:secret"));
        Assert.That(packed, Is.EqualTo(expected));
    }

    [Test]
    public void TryUnpack_NotBase64_ReturnsFalse()
    {
        var ok = DiscordCompatToken.TryUnpack("not-valid-base64!!!", out _, out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryUnpack_Base64ButNoColonSeparator_ReturnsFalse()
    {
        var packedWithoutColon = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("no-separator-here"));

        var ok = DiscordCompatToken.TryUnpack(packedWithoutColon, out _, out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryUnpack_SecretContainingColon_OnlySplitsOnFirstColon()
    {
        // A client secret is a random hex string in practice so this shouldn't happen,
        // but the split must not silently truncate the secret if it ever does.
        var packed = DiscordCompatToken.Pack("user_1", "sec:ret:with:colons");

        DiscordCompatToken.TryUnpack(packed, out var clientId, out var clientSecret);

        Assert.Multiple(() =>
        {
            Assert.That(clientId, Is.EqualTo("user_1"));
            Assert.That(clientSecret, Is.EqualTo("sec:ret:with:colons"));
        });
    }
}
