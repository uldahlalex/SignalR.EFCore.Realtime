using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SignalR.EFCore.Realtime;

/// <summary>
/// Delegate for executing a query that will be re-run when entity changes are detected.
/// </summary>
/// <typeparam name="TResult">The type of data returned by the query</typeparam>
/// <param name="services">The service provider for resolving dependencies</param>
/// <param name="authenticatedUserId">The ID of the authenticated user making the request</param>
/// <returns>The query result that will be broadcast to the client</returns>
public delegate Task<TResult> RealtimeQueryDelegate<TResult>(IServiceProvider services, string authenticatedUserId);

/// <summary>
/// Delegate for filtering which entity changes should trigger a query re-execution.
/// </summary>
/// <param name="changedEntity">The entity that was added, updated, or deleted</param>
/// <returns>True if this change should trigger the query to re-run and broadcast</returns>
public delegate bool EntityChangeFilter(object changedEntity);

/// <summary>
/// Helper methods for implementing realtime server-push pattern in SignalR hubs.
/// </summary>
public static class RealtimeQueryHelper
{
    /// <summary>
    /// Registers a reactive query subscription for a hub method.
    /// The server will automatically re-execute the query and push results when relevant data changes.
    /// </summary>
    /// <typeparam name="TResult">The type of data returned by the query</typeparam>
    /// <param name="hub">The SignalR hub instance</param>
    /// <param name="executeQuery">
    /// Function that executes the query. It will be called:
    /// <list type="bullet">
    /// <item>Initially when the hub method is invoked</item>
    /// <item>Each time a relevant entity change is detected</item>
    /// </list>
    /// Parameters: (services, authenticatedUserId) => Task&lt;TResult&gt;
    /// </param>
    /// <param name="shouldReactToChange">
    /// Optional filter to determine which entity changes should trigger re-execution.
    /// If null, any change to entities specified in [RealtimeQuery&lt;T&gt;] will trigger.
    /// Example: entity => entity is Game g && g.Id == gameId
    /// </param>
    /// <example>
    /// <code>
    /// [RealtimeQuery&lt;Game&gt;]
    /// public async Task&lt;GameReturnDto&gt; GetGameRealtime(GetGameRequestDto dto)
    /// {
    ///     var data = await quizService.GetGame(dto);
    ///
    ///     await this.EnableRealtime(
    ///         executeQuery: async (services, userId) => {
    ///             var service = services.GetRequiredService&lt;IQuizService&gt;();
    ///             return await service.GetGame(dto);
    ///         },
    ///         shouldReactToChange: entity => entity is Game g && g.Id == dto.GameId
    ///     );
    ///
    ///     return data;
    /// }
    /// </code>
    /// </example>
    public static async Task EnableRealtime<TResult>(
        this Hub hub,
        RealtimeQueryDelegate<TResult>? executeQuery = null,
        EntityChangeFilter? shouldReactToChange = null,
        [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {

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
        if (executeQuery == null)
        {
            throw new InvalidOperationException("executeQuery is required for reactive queries");
        }

        var serviceProvider = hub.Context.GetHttpContext()?.RequestServices
            ?? throw new InvalidOperationException("ServiceProvider not available");

        var interceptor = serviceProvider.GetRequiredService<RealtimeChangeInterceptor>();

        var userId = hub.Context.UserIdentifier
            ?? throw new InvalidOperationException("User must be authenticated for reactive queries");

        var connectionId = hub.Context.ConnectionId;

        // Get IHubContext once and capture it (it's a singleton, won't be disposed)
        var hubContextType = typeof(IHubContext<>).MakeGenericType(hubType);
        var hubContext = serviceProvider.GetRequiredService(hubContextType) as IHubContext
            ?? throw new InvalidOperationException($"Could not resolve IHubContext for {hubType.Name}");

        // Create broadcast callback that sends data to the specific client
        // We capture IHubContext (singleton) instead of Hub instance (scoped/disposed)
        Func<object, Task> broadcastCallback = async (data) =>
        {
            var clientsProxy = hubContext.Clients.Client(connectionId);
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
                QueryExecutor = async (serviceProvider, uid) => await executeQuery(serviceProvider, uid),
                BroadcastCallback = broadcastCallback,
                ChangeFilter = shouldReactToChange
            };

            interceptor.Subscribe(subscription);

            logger?.LogInformation(
                "✅ Registered reactive query: Method={Method}, ConnectionId={ConnectionId}, User={UserId}, EntityType={EntityType}",
                callingMethod.Name, connectionId, userId, entityType.Name);
        }
    }
}
