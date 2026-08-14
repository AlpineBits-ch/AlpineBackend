using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Guild.Application.Dtos.Response;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;

namespace Guild.Tests.Dtos;

/// <summary>
/// Guards the shape of the Facet-generated projections behind GET /guilds/{id}/me and GET
/// /guilds/{id}/members.
/// </summary>
[TestFixture]
public class MemberProjectionShapeTests
{
    private sealed class MemberAccessCollector : ExpressionVisitor
    {
        public List<MemberInfo> Accessed { get; } = [];

        protected override Expression VisitMember(MemberExpression node)
        {
            Accessed.Add(node.Member);
            return base.VisitMember(node);
        }
    }

    private static List<MemberInfo> AccessedMembers(Expression projection)
    {
        var collector = new MemberAccessCollector();
        collector.Visit(projection);
        return collector.Accessed;
    }

    private static Type? MemberType(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => null,
    };

    /// <summary>A collection navigation - an IEnumerable that isn't one of the scalar types that
    /// happen to implement it (string, byte[] for PublicKeyStore.Key).</summary>
    private static bool IsCollectionNavigation(MemberInfo member)
    {
        var type = MemberType(member);
        if (type is null || type == typeof(string) || type == typeof(byte[])) return false;
        return typeof(IEnumerable).IsAssignableFrom(type);
    }

    private static readonly Type[] ForeignAggregates =
        // Fully qualified: inside Guild.Tests.*, a bare "Domain." binds to Guild.Tests.Domain.
        [typeof(global::Guild.Domain.Aggregates.Guild), typeof(Channel), typeof(Category), typeof(Role), typeof(GuildInvite)];

    [Test]
    public void SelfMemberProjection_DoesNotWalkCollectionsOffForeignAggregates()
    {
        var offenders = AccessedMembers(SelfMemberDto.Projection)
            .Where(m => m.DeclaringType is not null && ForeignAggregates.Contains(m.DeclaringType))
            .Where(IsCollectionNavigation)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Distinct()
            .ToList();

        Assert.That(offenders, Is.Empty,
            "SelfMemberDto's projection reaches a collection on another aggregate. That is what made "
            + "GET /guilds/{id}/me 500 - keep every nested facet on this DTO scalar-only.");
    }

    [Test]
    public void MemberProjection_DoesNotWalkCollectionsOffForeignAggregates()
    {
        var offenders = AccessedMembers(MemberDto.Projection)
            .Where(m => m.DeclaringType is not null && ForeignAggregates.Contains(m.DeclaringType))
            .Where(IsCollectionNavigation)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Distinct()
            .ToList();

        // GuildInvite.Members was the live offender here: one raw GuildMember collection per row
        // of the member list.
        Assert.That(offenders, Is.Empty,
            "MemberDto's projection reaches a collection on another aggregate, which fans the member "
            + "list out into a cartesian join and leaks domain entities into the response.");
    }

    [Test]
    public void NeitherProjection_TouchesPublicKeys()
    {
        var touched = new[] { SelfMemberDto.Projection, (Expression)MemberDto.Projection }
            .SelectMany(AccessedMembers)
            .Where(m => m.Name == nameof(Channel.PublicKeys))
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .Distinct()
            .ToList();

        // Beyond the crash, these are users' encryption keys - they have no business in a
        // member payload at all.
        Assert.That(touched, Is.Empty);
    }

    [Test]
    public void SelfMemberProjection_StillCarriesRolePermissions()
    {
        // The flattening must not go so far that it strips the caller's roles: MaxDepth = 1 on
        // SelfMemberDto silently nulls out FlatRoleMember.Role, which is the whole point of the
        // RoleMembers collection being there.
        var reachesRolePermissions = AccessedMembers(SelfMemberDto.Projection)
            .Any(m => m.DeclaringType == typeof(Role) && m.Name == nameof(Role.Permissions));

        Assert.That(reachesRolePermissions, Is.True,
            "SelfMemberDto no longer projects Role.Permissions - clients can't resolve their own "
            + "permissions without it.");
    }

    /// <summary>Both halves of both masks, on both the role and the overwrite.</summary>
    [TestCase(typeof(Role), nameof(Role.Permissions))]
    [TestCase(typeof(Role), nameof(Role.ModulePermissions))]
    [TestCase(typeof(ChannelPermission), nameof(ChannelPermission.AllowPermissions))]
    [TestCase(typeof(ChannelPermission), nameof(ChannelPermission.DenyPermissions))]
    [TestCase(typeof(ChannelPermission), nameof(ChannelPermission.AllowModulePermissions))]
    [TestCase(typeof(ChannelPermission), nameof(ChannelPermission.DenyModulePermissions))]
    public void SelfMemberProjection_CarriesEveryPermissionMask(Type declaringType, string member)
    {
        var reached = AccessedMembers(SelfMemberDto.Projection)
            .Any(m => m.DeclaringType == declaringType && m.Name == member);

        Assert.That(reached, Is.True,
            $"SelfMemberDto's projection no longer reaches {declaringType.Name}.{member}.");
    }
}
