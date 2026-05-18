
using FluentValidation;
using Identity.Domain.Validators;

namespace Identity.Domain.ValueObjects;

public class Email
{
    public string Value { get; set; }
    
    public Email(string value)
    {
        Value = value;
        var validator = new EmailValidator();
        validator.ValidateAndThrow(this);
    }
}