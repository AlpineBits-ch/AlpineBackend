using System.Reflection;
using System.Text;
using Domain.Tests.Facets;
using Echo.Proxy;

namespace Domain.Tests.Routing;

/// <summary>
/// Every public path is a gateway path, and most gateway routes <b>strip</b> the service segment:
/// <c>/api/v1/identity/{**rest}</c> is forwarded to the Identity service as <c>/api/v1/{**rest}</c>.
/// So a service endpoint whose own route template already begins with its service segment is only
/// reachable at the absurd doubled path, and at the sensible one it 404s.
///
/// <para><b>This is not hypothetical and it is not loud.</b> Four Identity routes shipped in that
/// state: <c>mls-policy</c>, <c>admin/mls-certificate-coverage</c> and both halves of
/// <c>protection-level</c>. The certificate-enforcement kill switch was therefore inert in
/// production - and invisibly so, because a client that cannot read the policy correctly defaults to
/// <c>Observe</c>, which is also what a healthy fleet looks like. Protection level, a whole shipped
/// feature, simply did not exist over the wire. Nothing failed; things merely never happened.</para>
///
/// <para>Unit tests cannot catch this by construction: they invoke the endpoint method, and an
/// integration test that spins up one service calls it on its internal path. The mismatch only
/// exists in the seam between the gateway's route table and the service's, which is exactly what
/// this test reads - both sides, by reflection, with no list to maintain.</para>
/// </summary>
[TestFixture]
public class GatewayRouteContractTests
{
    /// <summary>
    /// Service segments whose doubled public path is deliberate and already shipped.
    ///
    /// <para><c>messaging</c> is a resource name as well as a service name: the message CRUD surface
    /// lives at <c>api/v1/messaging/...</c> inside the Messaging service, so its public path really is
    /// <c>/api/v1/messaging/messaging/...</c> and every client in the field calls it that way. Ugly,
    /// but load-bearing - "fixing" it is a breaking change to shipped builds for cosmetics. New
    /// endpoints in that service must keep the doubling rather than quietly diverging from it.</para>
    /// </summary>
    private static readonly HashSet<string> DeliberatelyDoubled = new(StringComparer.OrdinalIgnoreCase)
    {
        "messaging",
    };

    /// <summary>The attributes that declare an absolute route on our endpoints. Matched by type name
    /// so this does not need to reference Wolverine.Http or MVC, and keeps working if either moves
    /// the type.</summary>
    private static readonly string[] RouteAttributeSuffixes =
    [
        "WolverineGetAttribute", "WolverinePostAttribute", "WolverinePutAttribute",
        "WolverineDeleteAttribute", "WolverinePatchAttribute", "WolverineHeadAttribute",
        "WolverineOptionsAttribute", "RouteAttribute",
        "HttpGetAttribute", "HttpPostAttribute", "HttpPutAttribute", "HttpDeleteAttribute",
        "HttpPatchAttribute",
    ];

    [Test]
    public void No_service_route_repeats_the_gateway_segment_that_is_stripped_from_it()
    {
        var strippedSegments = StrippedGatewaySegments();
        var routes = DeclaredRoutes();

        // Both halves have to be non-empty or the test passes by discovering nothing, which is the
        // failure mode a reflection-driven guard actually has.
        Assert.That(strippedSegments, Is.Not.Empty,
            "No path-rewriting gateway routes were found. ProxyConfig reflection is broken - fix it "
            + "rather than deleting this test.");
        Assert.That(routes, Is.Not.Empty,
            "No endpoint routes were discovered at all. Route reflection is broken - fix it rather "
            + "than deleting this test.");

        var unreachable = (
            from route in routes
            let segment = FirstSegmentAfterApiV1(route.Template)
            where segment is not null
                  && strippedSegments.Contains(segment)
                  && !DeliberatelyDoubled.Contains(segment)
            select route with { Segment = segment }
        ).ToList();

        if (unreachable.Count > 0) Assert.Fail(Describe(unreachable));
    }

    private readonly record struct DeclaredRoute(string Template, string DeclaringType, string Assembly)
    {
        /// <summary>Filled in once the offending gateway segment is known; null until then.</summary>
        public string? Segment { get; init; }
    }

    /// <summary>
    /// The service segments the gateway removes on the way through.
    ///
    /// <para>Only routes that actually rewrite the path count. Federation deliberately does not - its
    /// <c>/api/v1/federation/events</c> is a protocol path a remote instance is told to post to, so
    /// the segment is part of the contract and the service owns it internally too.</para>
    /// </summary>
    private static HashSet<string> StrippedGatewaySegments()
    {
        var segments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in ProxyConfig.GetRoutes())
        {
            var path = route.Match.Path;
            if (path is null) continue;

            var segment = FirstSegmentAfterApiV1(path);
            if (segment is null || segment.StartsWith('{')) continue;

            var rewrites = route.Transforms?.Any(t =>
                t.Keys.Any(k => k.Contains("Path", StringComparison.OrdinalIgnoreCase))) ?? false;

            if (rewrites) segments.Add(segment);
        }

        return segments;
    }

    /// <summary><c>/api/v1/identity/mls-policy</c> -> <c>identity</c>; anything not under
    /// <c>api/v1</c>, and <c>api/v1/x</c> with no further segment, -> null.</summary>
    private static string? FirstSegmentAfterApiV1(string template)
    {
        var parts = template.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3) return null;
        if (!parts[0].Equals("api", StringComparison.OrdinalIgnoreCase)) return null;
        if (!parts[1].Equals("v1", StringComparison.OrdinalIgnoreCase)) return null;

        return parts[2];
    }

    private static IReadOnlyList<DeclaredRoute> DeclaredRoutes()
    {
        var routes = new List<DeclaredRoute>();

        foreach (var type in RepositoryTypes.All())
        {
            foreach (var template in TemplatesOn(type))
            {
                routes.Add(new DeclaredRoute(template, type.FullName ?? type.Name,
                    type.Assembly.GetName().Name ?? "?"));
            }

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.Static
                                          | BindingFlags.DeclaredOnly);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var method in methods)
            {
                foreach (var template in TemplatesOn(method))
                {
                    routes.Add(new DeclaredRoute(template, $"{type.FullName}.{method.Name}",
                        type.Assembly.GetName().Name ?? "?"));
                }
            }
        }

        return routes;
    }

    /// <summary>Route templates declared by attributes on a type or method, read from metadata so an
    /// attribute whose arguments reference an unloadable type still shows up.</summary>
    private static IEnumerable<string> TemplatesOn(MemberInfo member)
    {
        IList<CustomAttributeData> attributes;
        try
        {
            attributes = member.GetCustomAttributesData();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var attribute in attributes)
        {
            var name = attribute.AttributeType.Name;
            if (!RouteAttributeSuffixes.Contains(name)) continue;

            if (attribute.ConstructorArguments.Count == 0) continue;
            if (attribute.ConstructorArguments[0].Value is string template && template.Length > 0)
            {
                yield return template;
            }
        }
    }

    private static string Describe(IReadOnlyCollection<DeclaredRoute> unreachable)
    {
        var message = new StringBuilder();
        message.AppendLine(
            $"{unreachable.Count} endpoint route(s) repeat a gateway segment the gateway strips, so "
            + "they are unreachable at the path clients use:");
        message.AppendLine();

        foreach (var route in unreachable.OrderBy(r => r.Template, StringComparer.Ordinal))
        {
            var trimmed = route.Template.TrimStart('/');
            message.AppendLine($"  {trimmed}   ({route.DeclaringType}, {route.Assembly})");
            message.AppendLine(
                $"      public:  /api/v1/{route.Segment}/{trimmed}  ->  forwarded as /{trimmed}, no such route");
            message.AppendLine(
                $"      fix:     drop the leading '{route.Segment}/' from the route template, or give "
                + "the gateway route for this service a pass-through instead of a rewrite if the "
                + "segment is part of a published protocol path.");
        }

        return message.ToString();
    }
}
