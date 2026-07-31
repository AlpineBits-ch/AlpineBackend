using System.Reflection;
using System.Text;

namespace Domain.Tests.Facets;

/// <summary>
/// A facet (<c>[Facet(typeof(SomeEntity))]</c>) copies the properties of its source type at compile
/// time. When the source is an EF entity, every navigation property that is not excluded or
/// redirected to a nested facet is copied <em>as the entity type itself</em>. Serializing such a
/// facet then walks straight back into the tracked object graph - Guild -> Channel -> Guild - which
/// throws on a JSON reference loop, or silently drags half the database over the wire.
///
/// This test finds facets and entities by reflection only. There is no list to keep up to date:
/// a new facet, a new entity, a new DbSet or a whole new project is picked up automatically.
/// See Domain.Tests.csproj for how every project in the repo ends up loadable here.
/// </summary>
[TestFixture]
public class FacetEntityLeakTests
{
    [Test]
    public void No_facet_exposes_a_database_entity()
    {
        var types = RepositoryTypes.All();

        var entities = DatabaseEntities.Discover(types);
        var facets = types.Where(FacetTypes.IsFacet).OrderBy(t => t.FullName).ToList();

        // Guard against the whole test passing vacuously because discovery silently broke.
        Assert.That(
            facets,
            Is.Not.Empty,
            "No facets were discovered at all. Reflection-based discovery is broken - fix it "
                + "rather than deleting this test, otherwise nothing is being checked."
        );
        Assert.That(
            entities,
            Is.Not.Empty,
            "No EF entities were discovered at all. Reflection-based discovery is broken - fix it "
                + "rather than deleting this test, otherwise nothing is being checked."
        );

        var leaks = (
            from facet in facets
            from member in FacetTypes.SerializedMembers(facet)
            from referenced in TypeGraph.ReferencedTypes(member.Type)
            where entities.Contains(referenced)
            select new Leak(facet, member.Name, member.Type, referenced)
        ).ToList();

        if (leaks.Count > 0)
        {
            Assert.Fail(Describe(leaks));
        }
    }

    private sealed record Leak(Type Facet, string Member, Type MemberType, Type Entity);

    private static string Describe(IReadOnlyCollection<Leak> leaks)
    {
        var message = new StringBuilder();
        message.AppendLine(
            $"{leaks.Count} facet member(s) expose a database entity and can produce JSON reference loops:"
        );
        message.AppendLine();

        foreach (var group in leaks.GroupBy(l => l.Facet).OrderBy(g => g.Key.FullName))
        {
            message.AppendLine($"  {group.Key.FullName}  ({group.Key.Assembly.GetName().Name})");
            foreach (var leak in group.OrderBy(l => l.Member))
            {
                message.AppendLine(
                    $"    .{leak.Member} : {TypeGraph.Describe(leak.MemberType)}  ->  entity {leak.Entity.FullName}"
                );
            }
            message.AppendLine();
        }

        message.AppendLine("Fix each one on the facet, in order of preference:");
        message.AppendLine(
            "  1. Point the member at another facet: add the DTO to `NestedFacets` on the [Facet] attribute."
        );
        message.AppendLine(
            "  2. Drop the member: add it to `exclude:` on the [Facet] attribute, and expose the foreign key instead."
        );
        message.AppendLine(
            "  3. If the member genuinely must stay an entity and is never serialized, mark it [JsonIgnore]."
        );
        return message.ToString();
    }
}
