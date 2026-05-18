namespace Persistence;

public interface IPrefixedEntity
{
    static abstract string Prefix { get; }
}