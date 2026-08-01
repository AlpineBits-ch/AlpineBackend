namespace Domain.Tests.Bus;

/// <summary>
/// The rollout knobs must all start in the position that leaves an unmodified old client working.
/// </summary>
[TestFixture]
public class MlsPolicyDefaultsTests
{
    [Test]
    public void Certificate_enforcement_starts_at_Observe()
    {
        Assert.That(
            MlsPolicy.CertificateEnforcement,
            Is.EqualTo(CertificateEnforcement.Observe),
            "Advance this only on the coverage number from the admin endpoint. Enforcing below "
                + "roughly 99% coverage proposes the removal of real devices belonging to real users "
                + "whose only mistake was not having opened the app yet."
        );
    }

    [Test]
    public void Observe_is_the_zero_value_so_an_unparsable_policy_cannot_read_as_Enforce()
    {
        // A client that fails to parse the policy response deserializes the enum to its default.
        Assert.That((int)CertificateEnforcement.Observe, Is.Zero);
        Assert.That(default(CertificateEnforcement), Is.EqualTo(CertificateEnforcement.Observe));
    }

    [Test]
    public void The_breaking_contract_tightenings_are_all_off()
    {
        Assert.Multiple(() =>
        {
            // The legacy no-deviceId Welcome fetch is already non-consuming, which is the half of
            // the fix that removed the data loss.
            Assert.That(MlsPolicy.RequireDeviceIdOnWelcomeFetch, Is.False);

            // Old clients cannot pass ?allowPartialDeviceCoverage=true, so refusing by default
            // would take away their ability to create an encrypted conversation at all.
            Assert.That(MlsPolicy.RejectUnreachableDevicesOnCreate, Is.False);
        });
    }

    [Test]
    public void TrustedSignIn_is_the_default_protection_level()
    {
        // New accounts and every existing account at rollout.
        Assert.That(default(ProtectionLevel), Is.EqualTo(ProtectionLevel.TrustedSignIn));
    }
}
