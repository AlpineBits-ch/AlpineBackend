namespace Persistence;

public static class Ksuid
{
    public static string Generate(string type = "")
    {
        return KsuidDotNet.Ksuid.NewKsuid(type);
    }
}