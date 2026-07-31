using System.Reflection;

namespace Domain.Tests.Facets;

internal static class FacetTypes
{
    /// <summary>
    /// Matched by name, not by <c>typeof</c>: the attribute ships in Facet.Attributes, and matching
    /// on the name keeps this working across package upgrades and across assemblies that pin a
    /// different Facet version.
    /// </summary>
    private const string FacetAttribute = "Facet.FacetAttribute";

    private static readonly string[] JsonIgnoreAttributes =
    [
        "System.Text.Json.Serialization.JsonIgnoreAttribute",
        "Newtonsoft.Json.JsonIgnoreAttribute",
        "HotChocolate.GraphQLIgnoreAttribute",
    ];

    public static bool IsFacet(Type type)
    {
        try
        {
            // GetCustomAttributesData reads metadata without constructing the attribute, so a
            // facet whose attribute arguments reference an unloadable type still shows up.
            return type.GetCustomAttributesData()
                .Any(a => a.AttributeType.FullName == FacetAttribute);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public readonly record struct Member(string Name, Type Type);

    /// <summary>
    /// The public instance properties and fields of a facet that a serializer would write, both
    /// the ones the Facet generator emitted and the ones hand-written on the partial class.
    /// </summary>
    public static IEnumerable<Member> SerializedMembers(Type facet)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

        IEnumerable<Member> members;
        try
        {
            members = facet
                .GetProperties(flags)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead && !IsIgnored(p))
                .Select(p => new Member(p.Name, p.PropertyType))
                .Concat(
                    facet.GetFields(flags)
                        .Where(f => !IsIgnored(f))
                        .Select(f => new Member(f.Name, f.FieldType))
                );
        }
        catch (Exception)
        {
            return [];
        }

        return members.ToList();
    }

    private static bool IsIgnored(MemberInfo member) =>
        member
            .GetCustomAttributesData()
            .Any(a => JsonIgnoreAttributes.Contains(a.AttributeType.FullName));
}
