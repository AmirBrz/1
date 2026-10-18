using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Samian.Application.Configuration;
using Samian.Application.Contracts;
using Samian.Domain.Contracts;
using Samian.Infrastructure.Caching;
using Samian.Infrastructure.Messaging;
using Samian.Infrastructure.Notifications;
using Samian.Infrastructure.Outbox;
using Samian.Infrastructure.Persistence;
using Samian.Infrastructure.Persistence.Repositories;
using Samian.Infrastructure.Security;
using Samian.Infrastructure.Services;

namespace Samian.Infrastructure;

// The adapter layer's own registration extension, called once from the composition root. Every port
// declared inside Domain or Application gets exactly one implementation here — which is the testable
// statement of the architecture: if this file ever needs a `using` from WebApi, the onion is broken.
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        AddPersistence(services, configuration);
        AddCaching(services, configuration);
        AddSecurity(services, configuration);
        AddMessaging(services);
        AddPipeline(services);

        return services;
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SamianDatabase")
                               ?? throw new InvalidOperationException("ConnectionStrings:SamianDatabase is not configured.");

        // SQL Server per the brief; TrustServerCertificate because the container's cert is self-signed —
        // this is a DEV-only affordance of a local compose network, and the README says so.
        services.AddDbContext<SamianDbContext>(options => options.UseSqlServer(connectionString));

        // Scoped: one DbContext per request, shared by the repositories AND the unit of work, which is
        // what makes "stage in a repository, commit in the unit of work" a single transaction.
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ISaleEventRepository, SaleEventRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // The counter needs the scoped DbContext (it participates in the same connection).
        services.AddScoped<IDocumentNumberGenerator, DatabaseDocumentNumberGenerator>();

        // Stateless and thread-safe, so the longest lifetime is correct.
        services.AddSingleton<IClock, SystemClock>();
    }

    private static void AddCaching(IServiceCollection services, IConfiguration configuration)
    {
        services.AddMemoryCache();

        // abortConnect=false is NOT a way to hide a misconfiguration: Redis is a cache, and a cache that
        // is unreachable at startup must not prevent the application from serving reads from the
        // database. RedisCacheService then treats every failed L2 call as a miss (see that class).
        var redisConnectionString = configuration.GetConnectionString("Redis") ?? "localhost:6379,abortConnect=false";

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnectionString;

            // A key prefix so one Redis instance can host several applications without collisions, and
            // so `redis-cli --scan --pattern 'samian:*'` finds this application's keys.
            options.InstanceName = "samian:";
        });

        services.AddSingleton<ICacheService, RedisCacheService>();
        services.AddSingleton<IEmailSender, FileEmailSender>();
    }

    private static void AddSecurity(IServiceCollection services, IConfiguration configuration)
    {
        // Bound here (not in the host) because Infrastructure owns both the generation and the hashing,
        // and the host still binds the SAME section for JwtBearer validation — one source, two consumers.
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));

        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
    }

    private static void AddMessaging(IServiceCollection services)
    {
        // SCOPED, deliberately: the dispatcher resolves IEnumerable<IDomainEventHandler<TEvent>> from the
        // service provider, and those handlers take repositories. A singleton dispatcher would capture the
        // ROOT provider and hand handlers a DbContext from another request — the captive-dependency bug.
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
    }

    private static void AddPipeline(IServiceCollection services)
    {
        // Step 17: the outbox queue STAGES on the scoped context (no commit of its own), so it must
        // share the reservation's scope — same captive-dependency doctrine as the event dispatcher.
        services.AddScoped<IReservationJobQueue, ReservationJobQueue>();
        services.AddScoped<IPipelineInspector, PipelineInspector>();

        // The worker is a singleton HOST service that creates its own scopes — registering it here
        // keeps the wiring in Infrastructure while Program.cs only says AddHostedService<OutboxWorker>.
    }
}
