using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SignalR.EFCore.Realtime.Backplanes;
using StackExchange.Redis;

namespace SignalR.EFCore.Realtime;

/// <summary>
/// Extension methods for configuring the realtime system with different backplane implementations.
/// </summary>
public static class RealtimeServiceExtensions
{
    /// <summary>
    /// Configures the realtime system with an in-memory backplane (single-server mode).
    /// Use this for development, testing, or single-server deployments.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddRealtimeWithInMemory(this IServiceCollection services)
    {
        services.AddSingleton<IRealtimeBackplane, InMemoryBackplane>();
        return services;
    }

    /// <summary>
    /// Configures the realtime system with a Redis backplane (horizontal scaling mode).
    /// Use this for production deployments with multiple server instances.
    /// Requires Redis to be configured via AddSignalR().AddStackExchangeRedis() or manually.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="channelName">Optional custom channel name for Redis pub/sub. Defaults to "realtime:entity-changes"</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddRealtimeWithRedis(
        this IServiceCollection services,
        string? channelName = null)
    {
        services.AddSingleton<IRealtimeBackplane>(sp =>
        {
            var redis = sp.GetRequiredService<IConnectionMultiplexer>();
            var logger = sp.GetRequiredService<ILogger<RedisBackplane>>();
            return new RedisBackplane(redis, logger, channelName);
        });

        return services;
    }

    /// <summary>
    /// Configures the realtime system with a custom backplane implementation.
    /// Use this to provide your own backplane (e.g., RabbitMQ, PostgreSQL NOTIFY/LISTEN, etc.)
    /// </summary>
    /// <typeparam name="TBackplane">Your custom backplane implementation</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddRealtimeWithCustomBackplane<TBackplane>(
        this IServiceCollection services)
        where TBackplane : class, IRealtimeBackplane
    {
        services.AddSingleton<IRealtimeBackplane, TBackplane>();
        return services;
    }
}
