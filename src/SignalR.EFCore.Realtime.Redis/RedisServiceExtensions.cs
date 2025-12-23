using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SignalR.EFCore.Realtime.Backplanes;
using StackExchange.Redis;

namespace SignalR.EFCore.Realtime;

/// <summary>
/// Extension methods for configuring the realtime system with Redis backplane.
/// </summary>
public static class RedisServiceExtensions
{
    /// <summary>
    /// Configures the realtime system with a Redis backplane (horizontal scaling mode).
    /// Use this for production deployments with multiple server instances.
    /// Requires IConnectionMultiplexer to be registered in DI.
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
}
