namespace Samian.Application.Common;

// Every key carries its tenant. A cache is an "intermediate data layer" the brief requires to be
// namespaced (line 50): a shared key like product:{guid} across two tenants is the same isolation
// breach as a missing WHERE — except silent. Writers compute the SAME key method the readers use,
// which is why these stay methods, not interpolated strings scattered through services.
public static class CacheKeys
{
    public static string Product(Guid tenantId, Guid productId) => $"tenant:{tenantId}:product:{productId}";

    public static string ProductList(Guid tenantId, int page, int pageSize) => $"tenant:{tenantId}:product:list:{page}:{pageSize}";

    public static string Order(Guid tenantId, Guid orderId) => $"tenant:{tenantId}:order:{orderId}";

    public static string CustomerOrders(Guid tenantId, Guid customerId) => $"tenant:{tenantId}:customer:{customerId}:orders";

    // Two sale keys because they have different volatility: units change on EVERY reservation,
    // metadata changes when marketing does. One TTL for both means either a stale counter or
    // pointless reloads of static data.
    public static string SaleStatus(Guid tenantId, Guid saleEventId) => $"tenant:{tenantId}:sale:{saleEventId}:status";

    public static string SaleMetadata(Guid tenantId, Guid saleEventId) => $"tenant:{tenantId}:sale:{saleEventId}:metadata";
}

// TTLs are the STALENESS BUDGET the README documents (brief line 46): display values may lag by
// exactly these amounts. Reservations never read a cached value — the DB is the source of truth.
public static class CacheDurations
{
    public static readonly TimeSpan ProductReadModels = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan OrderReadModels = TimeSpan.FromMinutes(2);

    // Absolute expiration, not sliding: a hot key under burst traffic would never expire a sliding
    // TTL — "the sold-out badge appears minutes after it should" is the failure this choice prevents.
    public static readonly TimeSpan SaleStatus = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan SaleMetadata = TimeSpan.FromMinutes(5);
}