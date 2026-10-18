using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Samian.Application.Contracts;
using Samian.Domain.Entities;
using Samian.Domain.ValueObjects;
using Samian.Infrastructure.Persistence.Entities;

namespace Samian.Infrastructure.Persistence;

// Brings the database up and puts enough rows in it that the API is explorable the second it starts.
//
// Migrations replace EnsureCreated (ROADMAP step 14): MigrateAsync is versioned and idempotent —
// `docker-compose down && up` re-runs it harmlessly — which is half of the brief's
// "deterministic and repeatable" seed requirement (line 78); the per-table existence guards below
// are the other half.
//
// The seed data is DEV-ONLY (an admin password in source is a sample convenience, never a
// production pattern: real deployments get the first operator from a one-time bootstrap or an
// identity provider).
//
// NOTE on filters: the startup scope resolves the HTTP tenant provider OUTSIDE any request
// (HasTenant=false), so the global tenant filter short-circuits and seeding can write every
// tenant's rows. Seeding is the one process allowed to cross tenants — Step 19's middleware
// closes that door for requests, and this comment is where the README points from.
public static class DatabaseInitializer
{
    public const string AdminRole = "Admin";

    // The two tenants of the brief (line 70), with FIXED GUIDs: "down -v && up" must produce the
    // SAME ids or a reviewer's saved curl commands break between runs. ONE place defines them —
    // the registry row, every seeded product/customer/sale, and the README examples read from here.
    public static readonly Guid Electronics = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Fashion = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static async Task InitializeAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<SamianDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseInitializer));

        await db.Database.MigrateAsync(cancellationToken);

        var inserted = 0;

        // FIRST: everything below references the tenant ids.
        inserted += await SeedTenantsAsync(db, cancellationToken);
        inserted += await SeedUsersAsync(db, hasher, cancellationToken);
        inserted += await SeedProductsAsync(db, cancellationToken);
        inserted += await SeedCustomersAsync(db, cancellationToken);
        inserted += await SeedSaleEventsAsync(db, cancellationToken);

        if (inserted > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded {Count} rows into the Samian database.", inserted);
        }
    }

    private static async Task<int> SeedTenantsAsync(SamianDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Tenants.AnyAsync(cancellationToken))
            return 0;

        await db.Tenants.AddAsync(new TenantRow { Id = Electronics, Slug = "tenant-electronics" }, cancellationToken);
        await db.Tenants.AddAsync(new TenantRow { Id = Fashion, Slug = "tenant-fashion" }, cancellationToken);

        return 2;
    }

    private static async Task<int> SeedUsersAsync(SamianDbContext db, IPasswordHasher hasher, CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(cancellationToken))
            return 0;

        // Two accounts so [Authorize(Roles = "Admin")] can be demonstrated both ways: the manager can
        // write the catalogue, the clerk can only move documents through their lifecycle.
        // A user now BELONGS to a tenant (the credential carries it), so both are seeded into
        // Electronics — the app derives their tenant from the signed-in token, and stock/reservation
        // reads scope to it. (Users are exempt from the global filter; login has no ambient tenant.)
        await db.Users.AddAsync(User.Create(Electronics, "admin", hasher.Hash("Admin#1234"), AdminRole), cancellationToken);
        await db.Users.AddAsync(User.Create(Electronics, "salesclerk", hasher.Hash("Sales#1234")), cancellationToken);

        return 2;
    }

    private static async Task<int> SeedProductsAsync(SamianDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Products.AnyAsync(cancellationToken))
            return 0;

        // TEN products across the two tenants (brief line 75), realistic names and prices.
        // Electronics keeps the volume-policy-friendly spread (5 % above 1,000,000) so the discount
        // path is reachable with two or three lines; Fashion carries the sale-event SKUs.
        // The SKUs that match a seeded sale event are passed EXPLICITLY so the out-of-band warehouse
        // sync (OutboxWorker.SyncWarehouseAsync) can find and decrement the stocked row after a
        // reservation; the rest derive their SKU from the name.
        await db.Products.AddAsync(Product.Create("Mechanical Keyboard", new Money(850_000m, Order.DefaultCurrency), 40, Electronics), cancellationToken);
        await db.Products.AddAsync(Product.Create("27 inch Monitor", new Money(14_500_000m, Order.DefaultCurrency), 12, Electronics), cancellationToken);
        await db.Products.AddAsync(Product.Create("USB-C Dock", new Money(3_200_000m, Order.DefaultCurrency), 5, Electronics, "USB-C-DOCK"), cancellationToken);
        await db.Products.AddAsync(Product.Create("Wired Mouse", new Money(420_000m, Order.DefaultCurrency), 3, Electronics), cancellationToken);
        await db.Products.AddAsync(Product.Create("65 inch 4K TV", new Money(48_000_000m, Order.DefaultCurrency), 300, Electronics, "65INCH-TV"), cancellationToken);
        await db.Products.AddAsync(Product.Create("Noise-Cancelling Headphones", new Money(9_500_000m, Order.DefaultCurrency), 60, Electronics, "HEADPHONES-NC"), cancellationToken);

        await db.Products.AddAsync(Product.Create("Wool Overcoat", new Money(7_800_000m, Order.DefaultCurrency), 25, Fashion), cancellationToken);
        await db.Products.AddAsync(Product.Create("Leather Sneakers", new Money(5_200_000m, Order.DefaultCurrency), 80, Fashion, "SNEAKERS-LEATHER"), cancellationToken);
        await db.Products.AddAsync(Product.Create("Cashmere Scarf", new Money(2_400_000m, Order.DefaultCurrency), 120, Fashion, "SCARF-CASHMERE"), cancellationToken);
        await db.Products.AddAsync(Product.Create("Denim Jacket", new Money(3_900_000m, Order.DefaultCurrency), 45, Fashion, "DENIM-JACKET"), cancellationToken);

        return 10;
    }

    private static async Task<int> SeedCustomersAsync(SamianDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Customers.AnyAsync(cancellationToken))
            return 0;

        await db.Customers.AddAsync(
            Customer.Create("Nika Software", "Tehran, Vanak St, No. 12", "purchasing@nika.example", Electronics), cancellationToken);
        await db.Customers.AddAsync(
            Customer.Create("Saman Trading", "Isfahan, Chahar Bagh, No. 7", null, Electronics), cancellationToken);
        await db.Customers.AddAsync(
            Customer.Create("Moda Tiraz", "Tehran, Moda Ave, No. 4", null, Fashion), cancellationToken);

        return 3;
    }

    private static async Task<int> SeedSaleEventsAsync(SamianDbContext db, CancellationToken cancellationToken)
    {
        if (await db.SaleEvents.AnyAsync(cancellationToken))
            return 0;

        // ROADMAP step 21 warning honoured: the ACTIVE and UPCOMING windows are RELATIVE to now —
        // a fixed "9:00 AM on 2026-11-27" becomes expired the next morning and the reviewer sees
        // nothing to reserve. EXPIRED uses a fixed past window because "ended in the past" stays
        // true forever. Documented in the README.
        var now = DateTimeOffset.UtcNow;

        // tenant-electronics: one active (past -> future), one upcoming (future), one expired (past -> past).
        await db.SaleEvents.AddAsync(SaleEvent.Create(Electronics, "65INCH-TV", 150,
            new Money(31_200_000m, Order.DefaultCurrency), now.AddHours(-1), now.AddDays(1)), cancellationToken);   // 35 % off
        await db.SaleEvents.AddAsync(SaleEvent.Create(Electronics, "HEADPHONES-NC", 100,
            new Money(6_650_000m, Order.DefaultCurrency), now.AddDays(1), now.AddDays(2)), cancellationToken);      // upcoming
        await db.SaleEvents.AddAsync(SaleEvent.Create(Electronics, "USB-C-DOCK", 50,
            new Money(2_560_000m, Order.DefaultCurrency),
            new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.FromHours(3.5)),
            new DateTimeOffset(2026, 11, 27, 9, 30, 0, TimeSpan.FromHours(3.5))), cancellationToken);               // expired (last Black Friday)

        // tenant-fashion: same three states, so isolation can be proved with symmetric data.
        await db.SaleEvents.AddAsync(SaleEvent.Create(Fashion, "SCARF-CASHMERE", 100,
            new Money(1_440_000m, Order.DefaultCurrency), now.AddHours(-1), now.AddDays(1)), cancellationToken);
        await db.SaleEvents.AddAsync(SaleEvent.Create(Fashion, "SNEAKERS-LEATHER", 80,
            new Money(3_640_000m, Order.DefaultCurrency), now.AddDays(1), now.AddDays(2)), cancellationToken);
        await db.SaleEvents.AddAsync(SaleEvent.Create(Fashion, "DENIM-JACKET", 60,
            new Money(2_340_000m, Order.DefaultCurrency),
            new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.FromHours(3.5)),
            new DateTimeOffset(2026, 11, 27, 9, 30, 0, TimeSpan.FromHours(3.5))), cancellationToken);

        // The two ACTIVE events (150 and 100 units) are the load-test targets: 500 parallel
        // reservations against 65INCH-TV must sell exactly 150 — the concurrency test's contract
        // in real numbers (scripts/load-test.ps1 reads the sale id from GET /api/sales/active).
        return 6;
    }
}
