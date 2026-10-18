
using Samian.Domain.Entities;

namespace Samian.Domain.Contracts;
public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // Tenant-scoped on purpose: a user name is only unique WITHIN a tenant, and login carries the
    // tenant as part of the credential, so the lookup is the (tenantId, userName) pair — never the
    // ambient tenant (there is none yet at login). This is the explicit filter that replaces the
    // global one User is exempt from.
    Task<User?> GetByUserNameAsync(Guid tenantId, string userName, CancellationToken cancellationToken = default);
    Task AddAsync(User user, CancellationToken cancellationToken = default);
}
