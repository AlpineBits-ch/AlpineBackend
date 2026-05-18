using Persistence;

namespace Domain.Tests;

class MockUser : Aggregate<MockUser>, IPrefixedEntity
{
    public static string Prefix { get; } = "user";
}

public class Tests
{
    private readonly MockUser _user = new();
    

    [Test]
    public void Aggregate_Id_Generate_Should_Prefix_With_Underscore()
    {
       
        var id = MockUser.GenerateId();
        Assert.That(id.StartsWith("user_"));
        Assert.Pass();
    }
}