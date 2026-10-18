namespace Samian.Application.Contracts;

// WHICH tenant the current request belongs to. The HTTP mechanics (reading X-Tenant-Id,
// validating it, failing fast with 400) are a host concern — WebApi implements this port in
// exactly like ICurrentUser: Application asks, the adapter answers.
public interface ITenantProvider
{
    Guid TenantId { get; }

    bool HasTenant { get; }
}


internal sealed class NullTenantProvider : ITenantProvider
{
    public Guid TenantId => Guid.Empty;
    public bool HasTenant => false;
}