using Samian.Domain.Entities;

namespace Samian.Domain.Contracts;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // Batch load: StockAvailabilityService needs every product of an order in one round trip.
    // Loading them one by one inside a loop is how N+1 queries are born.
    Task<IReadOnlyList<Product>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Product>> GetPagedAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    Task AddAsync(Product product, CancellationToken cancellationToken = default);

    // Kept from your original contract. Note the nuance for the interview: with EF Core + a unit of
    // work, a tracked aggregate loaded via GetByIdAsync is persisted by SaveChangesAsync, so Update
    // matters mainly for detached/concurrency-checked paths (the challenge's optimistic-lock case).
    Task UpdateAsync(Product product, CancellationToken cancellationToken = default);

    Task DeleteAsync(Product product, CancellationToken cancellationToken = default);
}
