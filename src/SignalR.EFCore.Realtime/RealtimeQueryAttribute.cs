using Tapper;

namespace SignalR.EFCore.Realtime;

/// <summary>
/// Marks a hub method as a reactive query that automatically pushes updates when entities change.
/// Specifies which entity types this query depends on.
/// Use this attribute for query operations that need realtime updates.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class RealtimeQueryAttribute<TEntity> : Attribute
    where TEntity : class
{
}

/// <summary>
/// Request wrapper that indicates whether realtime mode should be enabled.
/// Add this as the last parameter to any hub method to support reactive queries.
/// </summary>
[TranspilationSource]
public class RealtimeOptions
{
    /// <summary>
    /// When true, the server will automatically push updates when relevant data changes.
    /// </summary>
    public bool Enabled { get; init; }
}

/// <summary>
/// Response wrapper for reactive queries.
/// </summary>
[TranspilationSource]
public class RealtimeResponse<T>
{
    /// <summary>
    /// The query result data.
    /// </summary>
    public required T Data { get; init; }
}
