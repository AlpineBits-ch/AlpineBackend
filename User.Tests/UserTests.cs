using FluentValidation;
using Identity.Domain.Aggregates;

namespace User.Tests;

public class Tests
{
    [SetUp]
    public void Setup()
    {
    }

   

    [Test]
    public void Invalid_Email_Must_Be_Rejected()
    {
        Assert.Throws<ValidationException>(() =>
        {
            Identity.Domain.Aggregates.ApplicationUser.Create(new CreateUserParams()
            {
                Username = "test",
                Email = "randomstuff",
                PhoneNumber = "+41782540276"
            });
        });
        
    }

    [Test]
    // TODO in around 10 years
    public void Must_Reject_Users_Below_14_Years_Old()
    {
        var simulatedDate = DateTime.Now;
        var dateOnly = DateOnly.FromDateTime(simulatedDate);
        var birthDate = dateOnly.AddYears(-13);

        Assert.Throws<ValidationException>(() =>
        {
            var user = Identity.Domain.Aggregates.ApplicationUser.Create(new CreateUserParams()
            {
                Username = "test",
                Email = "dominic@alpinebits.ch",
                PhoneNumber = "+41782540276",
                BirthDate = birthDate,
            });
        });


    }
}