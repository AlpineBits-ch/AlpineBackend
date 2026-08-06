using Guild.Domain.Services;

namespace Guild.Tests.Domain;

/// <summary>The one place the money path formats.</summary>
[TestFixture]
public class MoneyFormatTests
{
    [TestCase(4250, "CHF", "CHF 42.50")]
    [TestCase(5, "CHF", "CHF 0.05")]
    [TestCase(0, "EUR", "EUR 0.00")]
    [TestCase(100, "GBP", "GBP 1.00")]
    [TestCase(123456789, "USD", "USD 1234567.89")]
    public void FormatsTwoDecimalCurrencies(long minor, string currency, string expected) =>
        Assert.That(MoneyFormat.Format(minor, currency), Is.EqualTo(expected));

    /// <summary>Defaulting yen to two decimals would mis-render by a factor of a hundred.</summary>
    [TestCase(4250, "JPY", "JPY 4250")]
    [TestCase(4250, "KRW", "KRW 4250")]
    [TestCase(4250, "ISK", "ISK 4250")]
    public void FormatsZeroDecimalCurrencies(long minor, string currency, string expected) =>
        Assert.That(MoneyFormat.Format(minor, currency), Is.EqualTo(expected));

    /// <summary>Balances are signed - negative means they owe the house - and the sign belongs to
    /// the number, not the code.</summary>
    [TestCase(-4250, "CHF", "CHF -42.50")]
    [TestCase(-5, "CHF", "CHF -0.05")]
    [TestCase(-4250, "JPY", "JPY -4250")]
    public void CarriesTheSign(long minor, string currency, string expected) =>
        Assert.That(MoneyFormat.Format(minor, currency), Is.EqualTo(expected));

    [TestCase("chf", "CHF 1.00")]
    [TestCase(" chf ", "CHF 1.00")]
    [TestCase("", "CHF 1.00")]
    [TestCase(null, "CHF 1.00")]
    public void NormalisesTheCode(string? currency, string expected) =>
        Assert.That(MoneyFormat.Format(100, currency!), Is.EqualTo(expected));
}
