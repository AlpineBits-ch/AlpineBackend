namespace Isle.Domain.Exceptions;

public class GrowthTooLowException(double growth, double requiredGrowth) : Exception
{
    public double Growth { get; set; } = growth;
    public double RequiredGrowth { get; set; } = requiredGrowth;
}
