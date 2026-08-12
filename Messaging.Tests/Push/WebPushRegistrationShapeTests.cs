using Messaging.Application.Services;
using Messaging.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Messaging.Tests.Push;

/// <summary>How <see cref="WebPushSender"/> is registered, which is not a detail.</summary>
[TestFixture]
[Category("Unit")]
public class WebPushRegistrationShapeTests
{
    private static ServiceCollection Registered()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddWebPush();
        return services;
    }

    /// <summary>The assertion that matters.</summary>
    [Test]
    public void The_sender_is_registered_by_implementation_type_not_by_factory()
    {
        var descriptor = Registered().Single(d => d.ServiceType == typeof(WebPushSender));

        Assert.Multiple(() =>
        {
            Assert.That(descriptor.ImplementationType, Is.EqualTo(typeof(WebPushSender)),
                "Wolverine compiles a constructor call for this into the MessageCreated handler.");
            Assert.That(descriptor.ImplementationFactory, Is.Null,
                "An implementation factory forces service location, which Messaging refuses - and the "
                + "whole MessageCreated handler chain then fails to generate, silently.");
        });
    }

    /// <summary>
    /// Every constructor argument has to be resolvable too, or Wolverine hits the same wall one level
    /// down. <c>IHttpClientFactory</c> is the one that is easy to forget to register.
    /// </summary>
    [Test]
    public void Every_constructor_argument_of_the_sender_is_registered()
    {
        var services = Registered();
        var constructor = typeof(WebPushSender).GetConstructors().Single();

        foreach (var parameter in constructor.GetParameters())
        {
            // IMessageBus comes from Wolverine's own host and is not part of this extension method.
            if (parameter.ParameterType.Name == "IMessageBus") continue;

            Assert.That(
                services.Any(d => d.ServiceType == parameter.ParameterType
                                  || (parameter.ParameterType.IsGenericType
                                      && d.ServiceType == parameter.ParameterType.GetGenericTypeDefinition())),
                Is.True,
                $"{parameter.ParameterType.Name} is a constructor argument of WebPushSender and nothing "
                + "registers it.");
        }
    }

    /// <summary>Not the 100-second default: the sends are sequential and one unreachable push service
    /// would otherwise hold a whole fan-out.</summary>
    [Test]
    public void The_named_client_carries_a_short_timeout()
    {
        using var provider = Registered().BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(WebPushSender.HttpClientName);

        Assert.That(client.Timeout, Is.EqualTo(WebPushServiceCollectionExtensions.RequestTimeout));
        Assert.That(client.Timeout, Is.LessThan(TimeSpan.FromSeconds(30)));
    }

    /// <summary>The sender the name resolves is the sender, end to end - a named client nobody asks for
    /// under that exact name would leave the timeout above configured on nothing.</summary>
    [Test]
    public void The_sender_resolves()
    {
        var services = Registered();
        services.AddSingleton(Substitute());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.That(scope.ServiceProvider.GetRequiredService<WebPushSender>(), Is.Not.Null);
    }

    /// <summary>A do-nothing bus, only so the container has something to hand the sender.</summary>
    private static Wolverine.IMessageBus Substitute() => new FakeMessageBus(_ => null!);
}
