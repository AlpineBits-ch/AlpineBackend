namespace Identity.Domain.Enums;

[Flags]
public enum AgeVertificationLevel : ulong
{
    None = 0,
    SelfDeclaration = 1 << 0,
    AiEstimation = 1 << 1,
    GovermentId = 1 << 2
}