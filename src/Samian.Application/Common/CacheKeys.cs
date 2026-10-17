namespace Samian.Application.Common;

// Cache keys in ONE place, as methods instead of interpolated strings scattered through the services.
// The reason is invalidation: a writer must be able to compute the exact key a reader used. When keys
// are built ad hoc (`$"product-{id}"` here, `$"product:{id}"` there) writes silently fail to evict and
// the bug shows up as "the price is stale" five minutes later in production.
public static class CacheKeys
{
    // Namespaced with the entity name + ':' so a Redis instance shared by several services stays
    // browsable, and so KEYS product:* can list every product entry.
    public static string Product(Guid productId) => $"product:{productId}";

    public static string ProductList(int page, int pageSize) => $"product:list:{page}:{pageSize}";

    public static string Order(Guid orderId) => $"order:{orderId}";

    public static string CustomerOrders(Guid customerId) => $"customer:{customerId}:orders";
}

// TTLs as named values, not magic numbers at each call site: "reads are cheap and change rarely" is a
// policy, and a policy belongs where it can be argued about and changed once.
public static class CacheDurations
{
    public static readonly TimeSpan ProductReadModels = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan OrderReadModels = TimeSpan.FromMinutes(2);
}
