using Guild.Contracts;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

[TestFixture]
public class PermissionsParityTests
{
    // Wiki permissions are intentionally internal-only (not cross-service concerns),
    // so they're excluded from the external-contract parity check.
    private static readonly HashSet<string> ExcludedInternalNames =
    [
        nameof(Permissions.None),
        nameof(Permissions.ViewWiki),
        nameof(Permissions.CreateWikiPages),
        nameof(Permissions.EditOwnWikiPages),
        nameof(Permissions.EditAnyWikiPage),
        nameof(Permissions.DeleteWikiPages),
        nameof(Permissions.ManageWikiRevisions),
        nameof(Permissions.ManageWikiStructure),
        nameof(Permissions.ModerateWikiComments),
        nameof(Permissions.PublishWikiPublicly),
    ];

    [Test]
    public void EveryNonWikiInternalPermission_HasAMatchingExternalPermission()
    {
        var internalNames = Enum.GetNames<Permissions>()
            .Where(name => !ExcludedInternalNames.Contains(name))
            .ToHashSet();

        var externalNames = Enum.GetNames<ExternalPermission>().ToHashSet();

        var missing = internalNames.Except(externalNames).ToList();

        Assert.That(missing, Is.Empty,
            $"Permissions.{{{string.Join(", ", missing)}}} have no matching ExternalPermission. " +
            "Add the mirror value to Guild.Contracts/Permissions.cs and both MapToInternal switches.");
    }

    [Test]
    public void EveryExternalPermission_HasAMatchingInternalPermission()
    {
        var externalNames = Enum.GetNames<ExternalPermission>().ToHashSet();
        var internalNames = Enum.GetNames<Permissions>().ToHashSet();

        var missing = externalNames.Except(internalNames).ToList();

        Assert.That(missing, Is.Empty,
            $"ExternalPermission.{{{string.Join(", ", missing)}}} have no matching internal Permissions flag.");
    }
}
