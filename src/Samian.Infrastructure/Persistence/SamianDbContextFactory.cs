using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Samian.Infrastructure.Persistence;

// Design-time ONLY door: `dotnet ef migrations add` runs outside any request, where no ITenantProvider
// can exist. Runtime code never touches this — DI always resolves the two-arg constructor.
public sealed class SamianDbContextFactory : IDesignTimeDbContextFactory<SamianDbContext>
{
    public SamianDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SamianDbContext>()
            .UseSqlServer("Server=localhost,1433;Database=Samian;User Id=sa;Password=Samian!Passw0rd;TrustServerCertificate=True")
            .Options;
        return new SamianDbContext(options);
    }
}