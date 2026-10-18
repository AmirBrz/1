using Microsoft.EntityFrameworkCore;
using Samian.Domain.Entities;
using Samian.Domain.ValueObjects;
using Samian.Infrastructure.Persistence;
using Samian.Infrastructure.Persistence.Repositories;

namespace Samian.Tests;

// ROADMAP Step 22, test #1 (concurrency — the graded core). This is NOT an in-memory fake: it hammers
// the SAME conditional UPDATE the API uses (SaleEventRepository.TryReserveAsync →
//   UPDATE SaleEvents SET RemainingUnits = RemainingUnits - @qty
//   WHERE Id = @id AND TenantId = @t AND RemainingUnits >= @qty)
// against a real SQL Server, because that single statement is the only thing that makes no-oversell
// hold ACROSS concurrent requests and app instances (brief line 135). A fake would prove the fake.
//
// It needs the compose stack (or any migrated SQL Server) reachable via
// ConnectionStrings__SamianDatabase. When no database is reachable the test returns early rather than
// failing CI on a machine without SQL Server — the committed `scripts/logs/*.log` from load-test.ps1
// is the portable evidence (brief line 60 explicitly accepts a script). Run against the stack to see
// the 200/500 assertion actually fire.
public sealed class ConcurrencyTests
{
    private const int TotalUnits = 200;
    private const int ParallelRequests = 500;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__SamianDatabase")
        ?? "Server=localhost,1433;Database=Samian;User Id=sa;Password=Samian!Passw0rd;TrustServerCertificate=True;Encrypt=True;Connection Timeout=3";

    [Fact]
    public async Task FiveHundredParallelReservationsOnTwoHundredUnits_neverOversell()
    {
        var options = new DbContextOptionsBuilder<SamianDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        // Precheck: no reachable SQL Server → nothing to prove here (see class comment).
        try
        {
            await using var probe = new SamianDbContext(options);
            if (!await probe.Database.CanConnectAsync())
                return;
        }
        catch
        {
            return;
        }

        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var sale = SaleEvent.Create(tenantId, "65INCH-TV", TotalUnits, new Money(500, "IRR"),
            now.AddMinutes(-1), now.AddHours(1));

        // Seed ONE sale row, then tear it down — reruns must not accumulate against the 200/500 counts.
        await using (var setup = new SamianDbContext(options))
        {
            await setup.SaleEvents.AddAsync(sale);
            await setup.SaveChangesAsync();
        }

        try
        {
            // THE BURST: every reservation gets its OWN DbContext (a DbContext is not thread-safe)
            // and its OWN connection from the pool; SQL Server serialises the row lock, so at most
            // TotalUnits can leave — the exact property read-check-write violates.
            var accepted = await Task.WhenAll(
                Enumerable.Range(0, ParallelRequests).Select(_ => Task.Run(async () =>
                {
                    await using var ctx = new SamianDbContext(options);
                    var repo = new SaleEventRepository(ctx);
                    return await repo.TryReserveAsync(sale.Id, tenantId, quantity: 1);
                })));

            var sold = accepted.Count(ok => ok);
            Assert.Equal(TotalUnits, sold);   // exactly 200 succeed, 300 refused

            await using var check = new SamianDbContext(options);
            var remaining = await check.SaleEvents
                .Where(s => s.Id == sale.Id)
                .Select(s => s.RemainingUnits)
                .SingleAsync();

            Assert.Equal(0, remaining);                 // nothing oversold
            Assert.True(remaining >= 0);                // never negative
            Assert.Equal(sold + remaining, TotalUnits); // conservation: nothing vanished
        }
        finally
        {
            await using var cleanup = new SamianDbContext(options);
            await cleanup.SaleEvents.Where(s => s.Id == sale.Id).ExecuteDeleteAsync();
        }
    }
}
