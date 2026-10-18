namespace Samian.Application.Contracts;

// Caching is a PERFORMANCE concern, not a business rule, so it is an Application port (a domain port
// like IOrderRepository is owned by the Domain; this one is owned by the use cases). Application says
// "give me this read model, cache it for a while"; Infrastructure says whether that is Redis, memory
// or nothing at all.
//
// Deliberately generic over T and JSON-serialized in the adapter: no use case can tell whether it is
// talking to Redis, which is exactly what makes the adapter replaceable.
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class;

    Task SetAsync<T>(string key, T value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
        where T : class;

    // Invalidation is a first-class operation, not an afterthought: a write that does not remove its
    // keys is how a "Product price changed" update stays invisible for five minutes.
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    // Cache-aside as ONE call, so no use case can forget half of it (the classic bug is a Get with a
    // Set that lives somewhere else, or a factory that never populates).
    Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
        where T : class;
}

// Side effects that must NOT block the write (sending mail is slow and can fail; the order is already
// placed either way). Called from domain event handlers, never from a use case directly.
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

// ---------------- Reservation pipeline (ROADMAP step 17) ----------------

// The job PAYLOAD, not the storage row: Application says "this reservation must be fulfilled
// eventually"; the outbox table in Infrastructure decides how it survives. The payload is frozen
// at reservation time (sku, price, quantity) so the pipeline never has to re-read volatile state.
public sealed record ReservationJob(
    Guid TenantId,
    Guid SaleEventId,
    Guid ReservationId,
    string Sku,
    int Quantity,
    decimal UnitPriceAmount,
    string Currency,
    string CustomerRef);

// Staging is a PORT because "in the same transaction as the decrement" (brief line 21's guarantee)
// is a use-case rule; the outbox table is an adapter rule. The implementation MUST NOT commit —
// it stages on the same scoped DbContext and the unit of work's transaction is the only commit.
public interface IReservationJobQueue
{
    Task StageAsync(ReservationJob job, CancellationToken cancellationToken = default);
}

// Read model of the pipeline for GET /api/jobs — "observable" in the brief (line 39) means a
// reviewer can SEE Pending/Processing/Done/Dead without opening a database console.
public sealed record JobResponse(
    Guid Id,
    string Status,
    int Attempts,
    DateTimeOffset NextAt,
    string? Error,
    Guid SaleEventId,
    string Sku,
    int Quantity);

public interface IPipelineInspector
{
    // statusFilter: null = all; "Dead" = the DLQ view. Always scoped to the current tenant.
    Task<IReadOnlyList<JobResponse>> GetJobsAsync(string? statusFilter, CancellationToken cancellationToken = default);
}
