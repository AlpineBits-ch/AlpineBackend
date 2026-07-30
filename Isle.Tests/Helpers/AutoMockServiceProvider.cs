using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Isle.Tests.Helpers;

/// <summary>
/// A minimal auto-mocking <see cref="IServiceProvider"/> for exercising code that resolves an
/// open-ended set of types via <see cref="ActivatorUtilities"/> — e.g. <c>HelpCommand</c>, which
/// instantiates every registered chat command just to read its metadata. Explicit instances
/// (typically the test <c>MicroserviceContext</c>) win; every interface (including closed generics
/// like <c>ILogger&lt;Foo&gt;</c>) is served an NSubstitute fake, cached per type so repeated
/// resolutions return the same instance; every concrete class is constructed for real via
/// <see cref="ActivatorUtilities"/>, recursing into this same provider for its own constructor
/// parameters. This mirrors what the real DI container builds, just with every leaf interface faked.
/// </summary>
internal sealed class AutoMockServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _explicit = new();
    private readonly Dictionary<Type, object> _interfaceCache = new();

    public AutoMockServiceProvider With(Type type, object instance)
    {
        _explicit[type] = instance;
        return this;
    }

    public object? GetService(Type serviceType)
    {
        if (_explicit.TryGetValue(serviceType, out var explicitInstance))
            return explicitInstance;

        if (serviceType == typeof(IServiceProvider))
            return this;

        if (serviceType.IsInterface)
        {
            if (_interfaceCache.TryGetValue(serviceType, out var cached))
                return cached;

            var fake = Substitute.For([serviceType], []);
            _interfaceCache[serviceType] = fake;
            return fake;
        }

        // Concrete class: build it for real, recursing into this provider for its own dependencies —
        // exactly what a real DI container would do.
        return ActivatorUtilities.CreateInstance(this, serviceType);
    }
}
