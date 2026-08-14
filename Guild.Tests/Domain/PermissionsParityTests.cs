using Guild.Contracts;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

/// <summary>Keeps the internal permission enums and their external contract mirrors in step, in
/// both directions and for both masks. A missing mirror is not a compile error anywhere - it
/// surfaces as an ArgumentOutOfRangeException from one of the bus handlers' MapToInternal switches,
/// on whichever service happened to ask first.</summary>
[TestFixture]
public class PermissionsParityTests
{
    private static readonly HashSet<string> ExcludedInternalNames = [nameof(Permissions.None)];

    private static readonly HashSet<string> ExcludedInternalModuleNames = [nameof(ModulePermissions.None)];

    [Test]
    public void EveryInternalPermission_HasAMatchingExternalPermission()
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

    [Test]
    public void EveryInternalModulePermission_HasAMatchingExternalModulePermission()
    {
        var internalNames = Enum.GetNames<ModulePermissions>()
            .Where(name => !ExcludedInternalModuleNames.Contains(name))
            .ToHashSet();

        var externalNames = Enum.GetNames<ExternalModulePermission>().ToHashSet();

        var missing = internalNames.Except(externalNames).ToList();

        Assert.That(missing, Is.Empty,
            $"ModulePermissions.{{{string.Join(", ", missing)}}} have no matching ExternalModulePermission.");
    }

    [Test]
    public void EveryExternalModulePermission_HasAMatchingInternalModulePermission()
    {
        var externalNames = Enum.GetNames<ExternalModulePermission>().ToHashSet();
        var internalNames = Enum.GetNames<ModulePermissions>().ToHashSet();

        var missing = externalNames.Except(internalNames).ToList();

        Assert.That(missing, Is.Empty,
            $"ExternalModulePermission.{{{string.Join(", ", missing)}}} have no matching internal ModulePermissions flag.");
    }

    [Test]
    public void TheTwoExternalEnums_ShareNoNames()
    {
        // A name in both would mean the same word maps to two different internal masks depending on
        // which contract enum the caller reached for, and the compiler would not stop them picking
        // the wrong one.
        var core = Enum.GetNames<ExternalPermission>().ToHashSet();
        var module = Enum.GetNames<ExternalModulePermission>().ToHashSet();

        Assert.That(core.Intersect(module), Is.Empty);
    }

    [Test]
    public void TheTwoInternalEnums_ShareNoNamesBeyondNone()
    {
        var core = Enum.GetNames<Permissions>().ToHashSet();
        var module = Enum.GetNames<ModulePermissions>().ToHashSet();

        Assert.That(core.Intersect(module), Is.EquivalentTo(new[] { nameof(Permissions.None) }));
    }

    /// <summary>The ordinals of <see cref="ExternalPermission"/> cross the bus, so they are the
    /// contract - not the names, and not the declaration order. This pins the ones that survived
    /// the module split, which is exactly the set that would have silently renumbered had the
    /// removed members not been replaced with explicit values.</summary>
    [Test]
    public void ExternalPermission_OrdinalsSurvivedTheModuleSplit()
    {
        Assert.Multiple(() =>
        {
            Assert.That((int)ExternalPermission.ViewChannel, Is.EqualTo(0));
            Assert.That((int)ExternalPermission.ManageEvents, Is.EqualTo(29));
            Assert.That((int)ExternalPermission.Superadmin, Is.EqualTo(41));
            Assert.That((int)ExternalPermission.MentionEveryone, Is.EqualTo(42));
            Assert.That((int)ExternalPermission.ManageNicknames, Is.EqualTo(46));
        });
    }

    /// <summary>The numbers vacated by the household members must stay vacated.</summary>
    [Test]
    public void ExternalPermission_RetiredOrdinalsStayUnused()
    {
        int[] retired = [30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 47, 48, 49, 50];

        var used = Enum.GetValues<ExternalPermission>().Select(v => (int)v).ToHashSet();

        Assert.That(retired.Where(used.Contains), Is.Empty,
            "these numbers belonged to the household permissions that moved to ExternalModulePermission");
    }
}
