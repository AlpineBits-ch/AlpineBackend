namespace Domain.Tests.Bus;

/// <summary>
/// The rollout knobs must all start in the position that leaves an unmodified old client working.
///
/// <para>One of these defaults is not a preference. No device in the field carries an account-signed
/// certificate, so a client acting on <see cref="CertificateEnforcement.Enforce"/> would begin
/// proposing the removal of every other leaf in every group it is in - starting with its owner's
/// other devices - and nothing short of a client release would stop it. The safe value has to be the
/// value you get by doing nothing.</para>
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
        // If Enforce were zero, an unreachable server would be indistinguishable from an
        // instruction to start removing leaves.
        Assert.That((int)CertificateEnforcement.Observe, Is.Zero);
        Assert.That(default(CertificateEnforcement), Is.EqualTo(CertificateEnforcement.Observe));
    }

    [Test]
    public void The_breaking_contract_tightenings_are_all_off()
    {
        Assert.Multiple(() =>
        {
            // The legacy no-deviceId Welcome fetch is already non-consuming, which is the half of
            // the fix that removed the data loss. Requiring the parameter is the half that breaks
            // clients in the field, and it waits for minClientVersion.
            Assert.That(MlsPolicy.RequireDeviceIdOnWelcomeFetch, Is.False);

            // Old clients cannot pass ?allowPartialDeviceCoverage=true, so refusing by default
            // would take away their ability to create an encrypted conversation at all.
            Assert.That(MlsPolicy.RejectUnreachableDevicesOnCreate, Is.False);
        });
    }

    [Test]
    public void TrustedSignIn_is_the_default_protection_level()
    {
        // New accounts and every existing account at rollout. VerifiedDevices is opt-in, and cannot
        // be entered until every active device reports that it can enforce it.
        Assert.That(default(ProtectionLevel), Is.EqualTo(ProtectionLevel.TrustedSignIn));
    }
}
