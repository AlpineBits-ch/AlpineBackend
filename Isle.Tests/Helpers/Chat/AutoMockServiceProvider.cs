using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Isle.Tests.Helpers.Chat;

/// <summary>
/// Minimal auto-mocking DI container for tests that need to construct
/// <see cref="Isle.Api.Chat.ChatCommandRegistry"/>, which eagerly instantiates every registered
/// <c>ChatCommand</c> (via <c>ActivatorUtilities</c>) just to read its <c>Name</c>. Building that
/// real object graph by hand would mean wiring ~15 service classes most tests don't care about, so
/// instead: explicit overrides win, anything else resolves recursively — interfaces get an
/// NSubstitute stub, concrete classes get built via their widest public constructor. Resolved
/// instances are memoized so the same fake is reused everywhere it's depended on.
///
/// Implements <see cref="IServiceScopeFactory"/>/<see cref="IServiceScope"/> too so it can stand in
/// for the scope <c>ChatCommandRegistry</c> and <c>ChatCommandHandler</c> each create internally —
/// "creating a scope" just returns this same instance.
/// </summary>
internal sealed class ChatAutoMockServiceProvider : IServiceProvider, IServiceScopeFactory, IServiceScope
{
    private readonly Dictionary<Type, object> _instances = new();
    private readonly HashSet<Type> _resolving = [];

    public ChatAutoMockServiceProvider(params (Type Type, object Instance)[] overrides)
    {
        foreach (var (type, instance) in overrides)
            _instances[type] = instance;
    }

    public IServiceProvider ServiceProvider => this;
    public IServiceScope CreateScope() => this;
    public void Dispose()
    {
        // Nothing owns real resources here — overrides that need disposal are torn down by the test.
    }

    public object? GetService(Type serviceType)
    {
        if (_instances.TryGetValue(serviceType, out var existing))
            return existing;

        if (serviceType == typeof(IServiceProvider) || serviceType == typeof(IServiceScopeFactory))
            return this;

        if (!_resolving.Add(serviceType))
            throw new InvalidOperationException($"Circular dependency detected while resolving {serviceType}");

        try
        {
            object instance;
            if (serviceType.IsInterface || serviceType.IsAbstract)
            {
                instance = Substitute.For([serviceType], []);
            }
            else
            {
                var ctor = serviceType.GetConstructors()
                    .OrderByDescending(c => c.GetParameters().Length)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException($"No public constructor found for {serviceType}");

                var args = ctor.GetParameters()
                    .Select(p => GetService(p.ParameterType)
                        ?? throw new InvalidOperationException($"Could not resolve {p.ParameterType} (needed by {serviceType})"))
                    .ToArray();

                instance = ctor.Invoke(args);
            }

            _instances[serviceType] = instance;
            return instance;
        }
        finally
        {
            _resolving.Remove(serviceType);
        }
    }
}
