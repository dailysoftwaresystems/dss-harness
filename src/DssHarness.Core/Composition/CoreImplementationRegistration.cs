using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Core.Composition;

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
