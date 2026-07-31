namespace Domain.Tests.Facets;

internal static class TypeGraph
{
    /// <summary>
    /// Every type that appears in a member's declared type: the type itself plus whatever it wraps.
    /// </summary>
    public static IEnumerable<Type> ReferencedTypes(Type type)
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>();
        pending.Push(type);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;

            if (current.HasElementType && current.GetElementType() is { } element)
            {
                pending.Push(element);
            }

            if (current.IsGenericType)
            {
                foreach (var argument in current.GetGenericArguments())
                {
                    pending.Push(argument);
                }
            }
        }
    }

    /// <summary>C#-shaped name for assertion messages: <c>ICollection&lt;DamageMedia&gt;</c>.</summary>
    public static string Describe(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Describe(underlying) + "?";
        }

        if (type.IsArray && type.GetElementType() is { } element)
        {
            return Describe(element) + "[]";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`')];
        var arguments = string.Join(", ", type.GetGenericArguments().Select(Describe));
        return $"{name}<{arguments}>";
    }
}
