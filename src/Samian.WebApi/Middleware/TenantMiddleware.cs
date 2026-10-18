using Microsoft.EntityFrameworkCore;
using Samian.Application.Common;
using Samian.Application.Contracts;
using Samian.Infrastructure.Persistence;
using Samian.Infrastructure.Security;

namespace Samian.WebApi.Middleware;

// Step 19: resolves X-Tenant-Id ONCE per request, before any query can run. The roadmap rule:
// "missing or unknown tenant -> 400, and no query runs at all" — a forgotten tenant context must
// never fall through to an unfiltered query, because that failure is silent and cross-tenant.
// Both failure kinds throw AppValidationException so the ONE exception funnel answers 400 with
// Problem Details; there is no bespoke status handling in here (AGENTS.md: controllers and
// middleware stay out of the error-shape business).
public sealed class TenantMiddleware
{
    public const string HeaderName = "X-Tenant-Id";

    // The hand-off to HttpContextTenantProvider: middleware validates, provider reports. Two
    // classes because the pipeline stage and the DI port answer different questions — one runs
    // the request, the other is injected six layers deep where nothing knows HTTP exists.
    public const string ItemKey = "samian.tenant-id";

    private readonly RequestDelegate _next;

    public TenantMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        SamianDbContext db,
        ICacheService cache)
    {
        // Auth and Swagger are tenant-free by design; everything under /api is not.
        if (!context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/api/auth"))
        {
            await _next(context);
            return;
        }

        // THE tenant comes from the CREDENTIAL when there is one: an authenticated caller's token
        // carries a tenant_id claim, and that is authoritative — a client cannot act in a tenant it did
        // not sign into by editing a header. This runs after UseAuthorization, so User is populated (and
        // the signature already verified) by the time we read the claim. The header path below stays only
        // for anonymous /api callers (the load test) where no token exists yet.
        var claim = context.User?.FindFirst(JwtTokenGenerator.TenantClaimType)?.Value;
        if (context.User?.Identity?.IsAuthenticated == true
            && Guid.TryParse(claim, out var claimTenantId)
            && claimTenantId != Guid.Empty)
        {
            context.Items[ItemKey] = claimTenantId;
            await _next(context);
            return;
        }

        // Slug+id in one cached read: during the burst this middleware runs on every request and
        // the registry changes when onboarding does, not when reserving (TTL documented in
        // CacheDurations.TenantRegistry — the staleness trade-off, same rules as every other key).
        var registry = await cache.GetOrSetAsync<Dictionary<string, Guid>>(
            CacheKeys.TenantRegistry(),
            async () =>
            {
                // Concrete Dictionary (not IReadOnlyDictionary) for the same reason ProductList uses
                // List<T>: the adapter round-trips JSON, and only concrete classes deserialize.
                var rows = await db.Tenants
                    .Select(t => new { t.Id, t.Slug })
                    .ToListAsync(context.RequestAborted);
                return rows.ToDictionary(r => r.Slug, r => r.Id, StringComparer.OrdinalIgnoreCase);
            },
            CacheDurations.TenantRegistry,
            context.RequestAborted);

        var header = context.Request.Headers[HeaderName].ToString();

        // The header accepts the slug ("tenant-electronics") or the id — both names of the SAME
        // registry row. Unknown values answer the identical 400 either way: probing tenant
        // existence is answered the way login answers user enumeration.
        Guid tenantId;
        if (Guid.TryParse(header, out var id))
        {
            if (!registry.ContainsValue(id))
                throw Reject(header);
            tenantId = id;
        }
        else if (!registry.TryGetValue(header, out var slug))
        {
            throw Reject(header);
        }
        else
        {
            tenantId = slug;
        }

        context.Items[ItemKey] = tenantId;
        await _next(context);
    }

    private static AppValidationException Reject(string header) =>
        new($"'{header}' is not a known tenant. Send {HeaderName}: with a tenant slug or id " +
            "(see the seeded tenant-electronics / tenant-fashion).");
}
