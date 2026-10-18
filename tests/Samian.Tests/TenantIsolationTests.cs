using Samian.Application.Common;
using Samian.Domain.Entities;
using Samian.Domain.Exceptions;
using Samian.Domain.ValueObjects;

namespace Samian.Tests;

// ROADMAP Step 22, test #2 (tenant isolation). Two guarantees the brief cares about (line 50),
// provable without a database because both live in code the DB merely persists:
//   (a) the cache namespace separates tenants, so one tenant's cached badge can never be served to
//       another — the isolation breach a shared key would cause;
//   (b) a SaleEvent's counter belongs to ONE tenant and draining it cannot move another's.
public sealed class TenantIsolationTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SaleId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static SaleEvent OnSale(Guid tenantId, string sku, int units) =>
        SaleEvent.Create(tenantId, sku, units, new Money(500, "IRR"),
            Now.AddMinutes(-1), Now.AddHours(1));

    [Fact]
    public void Sale_status_cache_key_is_namespaced_per_tenant()
    {
        var keyA = CacheKeys.SaleStatus(TenantA, SaleId);
        var keyB = CacheKeys.SaleStatus(TenantB, SaleId);

        Assert.NotEqual(keyA, keyB);
        Assert.Contains($"tenant:{TenantA}", keyA);
        Assert.Contains($"tenant:{TenantB}", keyB);
    }

    [Fact]
    public void Reserving_to_zero_on_tenant_A_leaves_tenant_B_counter_untouched()
    {
        var saleA = OnSale(TenantA, "65INCH-TV", 100);
        var saleB = OnSale(TenantB, "SOFA", 150);

        var drained = saleA.TryReserve(100, Now);

        Assert.True(drained.IsSuccess);
        Assert.Equal(0, saleA.RemainingUnits);
        Assert.Equal(150, saleB.RemainingUnits);   // B never sees A's drain
    }

    [Fact]
    public void Sold_out_sale_refuses_further_units_and_never_goes_negative()
    {
        var sale = OnSale(TenantA, "65INCH-TV", 1);

        Assert.True(sale.TryReserve(1, Now).IsSuccess);
        var refused = sale.TryReserve(1, Now);      // the classic oversell attempt

        Assert.True(refused.IsFailure);
        Assert.Equal(0, sale.RemainingUnits);       // not -1: the guard stopped it
    }

    [Fact]
    public void Release_returns_units_but_cannot_exceed_the_original_allocation()
    {
        var sale = OnSale(TenantA, "65INCH-TV", 10);
        sale.TryReserve(10, Now);                    // 0 left

        sale.Release(4);

        Assert.Equal(4, sale.RemainingUnits);
        Assert.Throws<DomainException>(() => sale.Release(100));   // > TotalUnits
    }
}
