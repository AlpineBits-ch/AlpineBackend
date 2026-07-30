using System.Reflection;

namespace Isle.Tests.Helpers;

/// <summary>Invokes a `protected`-visibility method by reflection.</summary>
internal static class ProtectedInvoke
{
    private static MethodInfo Resolve(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (method is null)
            throw new MissingMethodException(target.GetType().FullName, methodName);
        return method;
    }

    /// <summary>Invokes a protected method returning <see cref="Task"/> and awaits it.</summary>
    public static async Task InvokeTaskAsync(object target, string methodName, params object?[] args)
    {
        var result = Resolve(target, methodName).Invoke(target, args);
        await (Task)result!;
    }

    /// <summary>Invokes a protected synchronous method and returns its result.</summary>
    public static T Invoke<T>(object target, string methodName, params object?[] args) =>
        (T)Resolve(target, methodName).Invoke(target, args)!;
}
