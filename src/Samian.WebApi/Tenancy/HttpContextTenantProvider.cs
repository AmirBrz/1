using Samian.Application.Contracts;
using Samian.WebApi.Middleware;

namespace Samian.WebApi.Tenancy;

// The HTTP adapter for the tenant port — same split as HttpContextCurrentUser: Application owns
// ITenantProvider, the host reads what TenantMiddleware already validated. The provider never
// re-parses the header: one door validates, everything downstream only consumes the answer.
//
// HasTenant=false outside HTTP (the seeder, and tomorrow's OutboxWorker scope) is the DESIGNED
// unfiltered path: those processes carry explicit tenant ids in their own queries, and the
// DbContext constructor turns this into `_tenantId == null`, which disables the global filter
// for exactly them — and for nobody who came through the pipeline with a request.
public sealed class HttpContextTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextTenantProvider(IHttpContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        _accessor = accessor;
    }

    public bool HasTenant => _accessor.HttpContext?.Items[TenantMiddleware.ItemKey] is Guid;

    public Guid TenantId =>
        _accessor.HttpContext?.Items[TenantMiddleware.ItemKey] is Guid id ? id : Guid.Empty;
}
