using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Docs.Generator;

/// <summary>
/// Turns a Roslyn <see cref="ITypeSymbol"/> into a JSON-Schema-shaped description of what actually
/// goes on the wire.
///
/// <para><b>Wire names, not C# names.</b> SignalR's JSON protocol serialises with a camelCase
/// naming policy, and none of the four <c>AddJsonProtocol</c> registrations in this solution
/// override it. So <c>ChannelId</c> ships as <c>channelId</c>, and a generator that emitted the
/// member names verbatim would document a wire format that does not exist.</para>
///
/// <para><b>Anonymous types are not a problem here.</b> <c>new { ChannelId = x }</c> compiles to a
/// real type with real members, so it is walked exactly like a named DTO. What anonymous payloads
/// cost is stability, not visibility.</para>
/// </summary>
internal sealed class SchemaBuilder
{
    /// <summary>Deep object graphs (Facet DTOs reach into EF navigation properties) would otherwise
    /// walk most of the domain model into one event's schema.</summary>
    private const int MaxDepth = 6;

    private readonly HashSet<string> _truncated = [];

    /// <summary>Types whose graph was cut short by the depth cap - reported rather than hidden, so a
    /// thin-looking schema is never mistaken for a complete one.</summary>
    public IReadOnlyCollection<string> Truncated => _truncated;

    public PayloadSchema Build(ITypeSymbol type) => Build(type, depth: 0, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    private PayloadSchema Build(ITypeSymbol type, int depth, HashSet<ITypeSymbol> seen)
    {
        var (unwrapped, nullable) = Unwrap(type);

        if (TryPrimitive(unwrapped, out var primitive))
            return primitive! with { Nullable = nullable };

        if (TryEnum(unwrapped, out var @enum))
            return @enum! with { Nullable = nullable };

        if (TryCollection(unwrapped, out var element))
        {
            return new PayloadSchema("array", Nullable: nullable)
            {
                Items = depth >= MaxDepth
                    ? new PayloadSchema("object")
                    : Build(element!, depth + 1, seen),
            };
        }

        // Cycles are real: EF navigation properties point back at their owner.
        if (!seen.Add(unwrapped))
            return new PayloadSchema("object", Nullable: nullable) { Note = $"circular ref to {Display(unwrapped)}" };

        if (depth >= MaxDepth)
        {
            _truncated.Add(Display(unwrapped));
            return new PayloadSchema("object", Nullable: nullable) { Note = "truncated at max depth" };
        }

        var node = new PayloadSchema("object", Nullable: nullable) { ClrType = Display(unwrapped) };

        foreach (var property in ReadableProperties(unwrapped))
        {
            node.Properties[WireName(property.Name)] = Build(property.Type, depth + 1, seen);
        }

        seen.Remove(unwrapped);
        return node;
    }

    /// <summary>Public, non-static, non-indexer, readable properties - what System.Text.Json would
    /// serialise.</summary>
    private static IEnumerable<IPropertySymbol> ReadableProperties(ITypeSymbol type)
    {
        var current = type;
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        while (current is not null && current.SpecialType != SpecialType.System_Object)
        {
            foreach (var member in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (member.DeclaredAccessibility != Accessibility.Public) continue;
                if (member.IsStatic || member.IsIndexer || member.GetMethod is null) continue;
                if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonIgnoreAttribute")) continue;
                if (!emitted.Add(member.Name)) continue;

                yield return member;
            }

            current = current.BaseType;
        }
    }

    public static string WireName(string clrName) => JsonNamingPolicy.CamelCase.ConvertName(clrName);

    private static (ITypeSymbol Type, bool Nullable) Unwrap(ITypeSymbol type)
    {
        // Nullable<T>
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } named)
            return (named.TypeArguments[0], true);

        // Reference type in a nullable-enabled context
        if (type.NullableAnnotation == NullableAnnotation.Annotated)
            return (type.WithNullableAnnotation(NullableAnnotation.NotAnnotated), true);

        return (type, false);
    }

    private static bool TryPrimitive(ITypeSymbol type, out PayloadSchema? node)
    {
        node = type.SpecialType switch
        {
            SpecialType.System_String or SpecialType.System_Char => new PayloadSchema("string"),
            SpecialType.System_Boolean => new PayloadSchema("boolean"),
            SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64 => new PayloadSchema("integer"),
            SpecialType.System_Single or SpecialType.System_Double
                or SpecialType.System_Decimal => new PayloadSchema("number"),
            _ => null,
        };

        if (node is not null) return true;

        node = Display(type) switch
        {
            "System.DateTime" or "System.DateTimeOffset" => new PayloadSchema("string") { Format = "date-time" },
            "System.DateOnly" => new PayloadSchema("string") { Format = "date" },
            "System.TimeOnly" or "System.TimeSpan" => new PayloadSchema("string") { Format = "duration" },
            "System.Guid" => new PayloadSchema("string") { Format = "uuid" },
            "System.Uri" => new PayloadSchema("string") { Format = "uri" },
            _ => null,
        };

        return node is not null;
    }

    /// <summary>
    /// Enums serialise as their member name wherever a <c>JsonStringEnumConverter</c> is registered
    /// - which is Guild, Isle, Messaging and Social, but <b>not</b> the gateway's own
    /// <c>AddSignalR()</c>. Recorded as a string with the member list plus a warning, because the
    /// representation genuinely depends on which process serialised the message.
    /// </summary>
    private static bool TryEnum(ITypeSymbol type, out PayloadSchema? node)
    {
        if (type.TypeKind != TypeKind.Enum)
        {
            node = null;
            return false;
        }

        var members = type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.ConstantValue is not null)
            .Select(f => f.Name)
            .ToList();

        node = new PayloadSchema("string")
        {
            ClrType = Display(type),
            Enum = members,
            Note = "int when serialised by a host without JsonStringEnumConverter (see the gateway)",
        };
        return true;
    }

    private static bool TryCollection(ITypeSymbol type, out ITypeSymbol? element)
    {
        if (type is IArrayTypeSymbol array)
        {
            element = array.ElementType;
            return true;
        }

        // A string is IEnumerable<char>; it is not a collection for these purposes.
        if (type.SpecialType == SpecialType.System_String)
        {
            element = null;
            return false;
        }

        var enumerable = (type as INamedTypeSymbol)?.AllInterfaces
            .FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);

        if (enumerable is not null)
        {
            element = enumerable.TypeArguments[0];
            return true;
        }

        element = null;
        return false;
    }

    private static string Display(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
            SymbolDisplayGlobalNamespaceStyle.Omitted));
}

/// <summary>One node of a payload schema.</summary>
internal sealed record PayloadSchema(string Type, bool Nullable = false)
{
    public string? Format { get; init; }
    public string? ClrType { get; set; }
    public string? Note { get; set; }
    public IReadOnlyList<string>? Enum { get; init; }
    public PayloadSchema? Items { get; set; }
    public Dictionary<string, PayloadSchema> Properties { get; } = new(StringComparer.Ordinal);
}
