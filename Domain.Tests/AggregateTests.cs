using Persistence;

namespace Domain.Tests;

class MockUser : Aggregate<MockUser>, IPrefixedEntity
{
    // "mock", not "user": IdGenerationArchitectureTests asserts prefixes are unique across the
    // solution, and this test double would otherwise collide with ApplicationUser's real one.
    public static string Prefix { get; } = "mock";
}

public class Tests
{
    private readonly MockUser _user = new();
    

    [Test]
    public void Aggregate_Id_Generate_Should_Prefix_With_Underscore()
    {
       
        var id = MockUser.GenerateId();
        Assert.That(id.StartsWith("mock_"));
        Assert.Pass();
    }
}