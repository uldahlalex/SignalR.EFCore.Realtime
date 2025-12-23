using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SignalR.EFCore.Realtime;

/// <summary>
/// Helper methods for implementing realtime server-push pattern in SignalR hubs.
/// </summary>
public static class RealtimeQueryHelper
{
    /// <summary>
    /// Wraps a query result and registers a reactive query subscription.
    /// The server will automatically re-execute the query and push results when relevant data changes.
    ///
    /// Usage in hub methods:
    /// <code>
    /// [RealtimeQuery&lt;Game&gt;]
    /// public async Task&lt;RealtimeResponse&lt;GameReturnDto&gt;&gt; GetGameRealtime(GetGameRequestDto dto, RealtimeOptions? options = null)
    /// {
    ///     var data = await quizService.GetGame(dto);
    ///     return await this.WrapRealtime(
    ///         data,
    ///         options,
    ///         queryExecutor: async (sp, uid) => await quizService.GetGame(dto),
    ///         changeFilter: entity => entity is Game g && g.Id == dto.GameId
    ///     );
    /// }
    /// </code>
    /// </summary>
    public static async Task<RealtimeResponse<T>> WrapRealtime<T>(
        this Hub hub,
        T data,
        RealtimeOptions? options = null,
        Func<IServiceProvider, string, Task<T>>? queryExecutor = null,
        Func<object, bool>? changeFilter = null,
        [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        if (options?.Enabled != true)
        {
            return new RealtimeResponse<T> { Data = data };
        }

        var logger = hub.Context.GetHttpContext()?.RequestServices
            .GetService<ILogger<RealtimeChangeInterceptor>>();

        // Get the calling method using CallerMemberName
        var hubType = hub.GetType();
        var callingMethod = hubType.GetMethod(callerName);

        if (callingMethod == null)
        {
            throw new InvalidOperationException($"Could not find method '{callerName}' on hub type '{hubType.Name}'");
        }

        // Get broadcast handler from [RealtimeQuery<TEntity, TDto>] attribute
        var realtimeQueryAttrs = callingMethod.GetCustomAttributes(false)
            .Where(a => a.GetType().IsGenericType &&
                        a.GetType().GetGenericTypeDefinition().Name.StartsWith("RealtimeQueryAttribute"))
            .ToList();

        if (!realtimeQueryAttrs.Any())
        {
            throw new InvalidOperationException(
                $"Method {callerName} must have [RealtimeQuery<TEntity, TDto>] attribute to use realtime mode");
        }

        // Get entity types from all handlers (for reactive query registration)
        var entityTypes = new List<Type>();
        foreach (var attr in realtimeQueryAttrs)
        {
            var attrType = attr.GetType();
            var genericArgs = attrType.GetGenericArguments();
            if (genericArgs.Length >= 1)
            {
                entityTypes.Add(genericArgs[0]); // TEntity
            }
        }

        // Server-push mode: Register reactive query subscription (server re-executes query on changes)
        if (queryExecutor == null)
        {
            throw new InvalidOperationException("queryExecutor is required for reactive queries");
        }

        var interceptor = hub.Context.GetHttpContext()?.RequestServices
            .GetRequiredService<RealtimeChangeInterceptor>();

        if (interceptor == null)
        {
            throw new InvalidOperationException("RealtimeChangeInterceptor not available in DI");
        }

        var userId = hub.Context.UserIdentifier
            ?? throw new InvalidOperationException("User must be authenticated for reactive queries");

        // Create broadcast callback that sends data to the specific client
        var connectionId = hub.Context.ConnectionId;
        Func<object, Task> broadcastCallback = async (data) =>
        {
            var clientsProxy = hub.Clients.Client(connectionId);
            await clientsProxy.SendCoreAsync("OnBroadcast", new[] { data });
        };

        // Register subscription for each entity type
        foreach (var entityType in entityTypes)
        {
            var subscription = new ReactiveQuerySubscription
            {
                ConnectionId = connectionId,
                UserId = userId,
                EntityType = entityType,
                QueryExecutor = async (serviceProvider, uid) => await queryExecutor(serviceProvider, uid),
                BroadcastCallback = broadcastCallback,
                ChangeFilter = changeFilter
            };

            interceptor.Subscribe(subscription);

            logger?.LogInformation(
                "✅ Registered reactive query: Method={Method}, ConnectionId={ConnectionId}, User={UserId}, EntityType={EntityType}",
                callingMethod.Name, connectionId, userId, entityType.Name);
        }

        return new RealtimeResponse<T>
        {
            Data = data
        };
    }
}
