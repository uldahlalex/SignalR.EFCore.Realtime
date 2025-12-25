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
