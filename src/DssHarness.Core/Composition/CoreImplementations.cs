using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Core.Composition;

/// <summary>
/// Every implementation of the contract <typeparamref name="T"/> the core holds, found by one scan of the core: what the
/// composition root registers, and what a registry of this build is made from where no container is asked - so an
/// implementation is registered by being written. The one scan of every contract the core is open to: the codebase
/// graph's languages, the web search's kinds.
/// </summary>
/// <typeparam name="T">The contract.</typeparam>
public static class CoreImplementations<T>
    where T : class
{
    private static IReadOnlyList<T>? _ofThisBuild;

    /// <summary>Every concrete type of the core that implements <typeparamref name="T"/>, by its full name.</summary>
    public static IReadOnlyList<Type> Types { get; } =
        [.. typeof(T).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(T).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)];

    /// <summary>One of each, made with no argument as the container makes it: for what has no container to ask - help.</summary>
    public static IReadOnlyList<T> OfThisBuild
        => LazyInitializer.EnsureInitialized(ref _ofThisBuild, () => [.. Types.Select(type => (T)Activator.CreateInstance(type)!)]);
}

/// <summary>The registration of a contract's implementations, as <see cref="CoreImplementations{T}"/> finds them.</summary>
public static class CoreImplementationRegistration
{
    /// <summary>Registers every implementation of <typeparamref name="T"/> the core holds, each once, as one service of it.</summary>
    /// <typeparam name="T">The contract.</typeparam>
    /// <param name="services">The container's registrations.</param>
    public static IServiceCollection AddCoreImplementationsOf<T>(this IServiceCollection services)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (var type in CoreImplementations<T>.Types)
        {
            services.AddSingleton(typeof(T), type);
        }

        return services;
    }
}
