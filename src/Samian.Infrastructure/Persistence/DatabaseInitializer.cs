using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Samian.Application.Contracts;
using Samian.Domain.Entities;
using Samian.Domain.ValueObjects;

namespace Samian.Infrastructure.Persistence;

// Brings the database up and puts enough rows in it that the API is explorable the second it starts.
//
// EnsureCreated instead of migrations: this is a sample whose schema is proved by running it, and
// `dotnet ef migrations add` needs the design-time package plus a design-time factory. In a real
// product the opposite is true — migrations are the only acceptable answer, because EnsureCreated
// cannot evolve an existing schema and will happily delete a column you added by hand. The line to
// switch is this one method; nothing else in the solution touches DDL.
//
// The seed data is DEV-ONLY (an admin password in source is a deliberate sample convenience, never a
// production pattern: real deployments get the first operator from a one-time bootstrap or an
// identity provider).
public static class DatabaseInitializer
{
    public const string AdminRole = "Admin";

    public static async Task InitializeAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<SamianDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseInitializer));

        // await db.Database.EnsureCreatedAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);


        var inserted = 0;

        inserted += await SeedUsersAsync(db, hasher, cancellationToken);
        inserted += await SeedProductsAsync(db, cancellationToken);
        inserted += await SeedCustomersAsync(db, cancellationToken);

        if (inserted > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded {Count} rows into the Samian sample database.", inserted);
        }
    }

    private static async Task<int> SeedUsersAsync(SamianDbContext db, IPasswordHasher hasher, CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(cancellationToken))
            return 0;

        // Two accounts so [Authorize(Roles = "Admin")] can be demonstrated both ways: the manager can
        // write the catalogue, the clerk can only move documents through their lifecycle.
        await db.Users.AddAsync(User.Create("admin", hasher.Hash("Admin#1234"), AdminRole), cancellationToken);
        await db.Users.AddAsync(User.Create("salesclerk", hasher.Hash("Sales#1234")), cancellationToken);

        return 2;
    }

    private static async Task<int> SeedProductsAsync(SamianDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Products.AnyAsync(cancellationToken))
            return 0;

        // Prices chosen so the default volume policy (5 % above 1,000,000) is reachable with two or
        // three lines: a reviewer can see the discount path work without doing arithmetic.
        await db.Products.AddAsync(Product.Create("Mechanical Keyboard", new Money(850_000m, Order.DefaultCurrency), 40), cancellationToken);
        await db.Products.AddAsync(Product.Create("27 inch Monitor", new Money(14_500_000m, Order.DefaultCurrency), 12), cancellationToken);
        await db.Products.AddAsync(Product.Create("USB-C Dock", new Money(3_200_000m, Order.DefaultCurrency), 5), cancellationToken);
        await db.Products.AddAsync(Product.Create("Wired Mouse", new Money(420_000m, Order.DefaultCurrency), 3), cancellationToken);

        return 4;
    }

    private static async Task<int> SeedCustomersAsync(SamianDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Customers.AnyAsync(cancellationToken))
            return 0;

        await db.Customers.AddAsync(
            Customer.Create("Nika Software", "Tehran, Vanak St, No. 12", "purchasing@nika.example"), cancellationToken);
        await db.Customers.AddAsync(
            Customer.Create("Saman Trading", "Isfahan, Chahar Bagh, No. 7"), cancellationToken);

        return 2;
    }
}
