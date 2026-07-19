using Domain;
using Persistence;

namespace Isle.Domain.Aggregates;

public class IsleServer : Aggregate<IsleServer>, IPrefixedEntity
{
    public string Name { get; set; }
    public string Address { get; set; }
    public static string Prefix { get; } = "ilse";
}