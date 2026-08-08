using System.Net;
using System.Text;
using System.Text.Json;
using AppEnvironment;
using Guild.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Guild.Tests.Helpers;

/// <summary>Everything needed to exercise the live product lookup without a network.</summary>
internal static class ProductCatalogHarness
{
    /// <summary>
    /// A lookup service wired to <paramref name="handler"/>, with a bucket that will grant
    /// <paramref name="budget"/> tokens and then refuse.
    /// </summary>
    public static ProductCatalogLookupService Lookups(HttpMessageHandler handler, int budget = 1000) =>
        new(new StubHttpClientFactory(handler), Limiter(budget),
            NullLogger<ProductCatalogLookupService>.Instance);

    public static ProductCatalogRateLimiter Limiter(int budget) =>
        new(RedisWithBudget(budget), NullLogger<ProductCatalogRateLimiter>.Instance);

    /// <summary>A multiplexer whose token-bucket script grants <paramref name="budget"/> times and
    /// then denies, which is what the real script does once the bucket is empty and the minute is
    /// not yet up.</summary>
    public static IConnectionMultiplexer RedisWithBudget(int budget)
    {
        var remaining = budget;

        var database = Substitute.For<IDatabase>();

        database
            .ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(_ => Task.FromResult(
                RedisResult.Create((RedisValue)(Interlocked.Decrement(ref remaining) >= 0 ? 1 : 0))));

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        return multiplexer;
    }

    /// <summary>A limiter whose Redis is down, for the fail-closed case.</summary>
    public static ProductCatalogRateLimiter LimiterWithNoRedis()
    {
        var broken = Substitute.For<IConnectionMultiplexer>();

        broken.GetDatabase(Arg.Any<int>(), Arg.Any<object?>())
            .Returns(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        return new ProductCatalogRateLimiter(broken, NullLogger<ProductCatalogRateLimiter>.Instance);
    }

    /// <summary>
    /// Runs something with live lookup switched on and a contact address configured, then puts the
    /// environment back.
    /// </summary>
    public static async Task<T> WithLiveLookupAsync<T>(
        Func<Task<T>> action, TimeSpan? inlineTimeout = null, bool enabled = true)
    {
        var config = Env.ProductCatalog;

        var previousEmail = config.ContactEmail;
        var previousFlag = config.LiveFillEnabled;
        var previousTimeout = config.InlineTimeout;

        config.LiveFillEnabled = enabled;
        config.ContactEmail = "tests@venta.gg";
        if (inlineTimeout is { } timeout) config.InlineTimeout = timeout;

        try
        {
            return await action();
        }
        finally
        {
            config.LiveFillEnabled = previousFlag;
            config.ContactEmail = previousEmail;
            config.InlineTimeout = previousTimeout;
        }
    }

    public static async Task WithLiveLookupAsync(
        Func<Task> action, TimeSpan? inlineTimeout = null, bool enabled = true) =>
        await WithLiveLookupAsync<object?>(async () =>
        {
            await action();
            return null;
        }, inlineTimeout, enabled);

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://catalog.test/") };
    }
}

/// <summary>A stand-in for the product API that records what it was asked and answers however the
/// test says, including by never answering at all.</summary>
internal sealed class StubProductApi(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<string> Requested { get; } = [];

    /// <summary>A fixed status and body, which is every case except the hang.</summary>
    public static StubProductApi Answers(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    /// <summary>
    /// The v3 shape for a product that exists: a string status, and the product under its own key
    /// with the source's own field names.
    /// </summary>
    public static StubProductApi Finds(
        string barcode, string germanName, string? brand = null, string productType = "food") =>
        Answers(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            status = "success",
            code = barcode,
            result = new { id = "product_found" },
            product = new
            {
                product_name_de = germanName,
                brands = brand,
                product_quantity = 380,
                product_quantity_unit = "g",
                product_type = productType,
            },
        }));

    /// <summary>The v3 shape for a barcode nobody has contributed: 404 with a failure status.</summary>
    public static StubProductApi FindsNothing() =>
        Answers(HttpStatusCode.NotFound,
            """{"status":"failure","result":{"id":"product_not_found"}}""");

    /// <summary>
    /// The v3 shape for "that barcode is real, but it lives in another database".
    /// </summary>
    public static StubProductApi FindsInAnotherFlavour(string productType = "beauty") =>
        Answers(HttpStatusCode.NotFound, JsonSerializer.Serialize(new
        {
            status = "failure",
            result = new { id = "product_found_with_a_different_product_type" },
            errors = new[]
            {
                new
                {
                    field = new { id = "product_type", value = productType },
                    message = new { id = "product_found_with_a_different_product_type" },
                },
            },
        }));

    /// <summary>Never answers.</summary>
    public static StubProductApi Hangs() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("A hanging stub was asked to produce a response");
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requested.Add(request.RequestUri!.ToString());
        return await respond(request, cancellationToken);
    }
}
