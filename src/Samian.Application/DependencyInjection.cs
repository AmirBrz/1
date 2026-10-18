using Microsoft.Extensions.DependencyInjection;
using Samian.Application.Configuration;
using Samian.Application.Contracts;
using Samian.Application.Events;
using Samian.Application.Features.Auth;
using Samian.Application.Features.Customers;
using Samian.Application.Features.Orders;
using Samian.Application.Features.Products;
using Samian.Application.Features.Reservations;
using Samian.Application.Features.Sales;
using Samian.Application.Validation;
using Samian.Domain.Contracts;
using Samian.Domain.Events;
using Samian.Domain.Services;

namespace Samian.Application;

// The application layer's OWN registration extension. The host (WebApi/Program.cs) calls
// `builder.Services.AddApplication(...)` and `AddInfrastructure(...)`: the composition root wires the
// layers, no layer ever reaches into another to find out what is registered (and no ServiceLocator).
public static class DependencyInjection
{
    // PricingOptions is passed in rather than read from IConfiguration here: Application holds only
    // DependencyInjection.Abstractions, so it cannot bind configuration sections. The host binds and
    // hands the values over — one reason the host is the only place that knows about appsettings.
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        PricingOptions pricingOptions)
    {
        AddPricingStrategy(services, pricingOptions);

        // ---------------- Use cases (scoped: one database transaction per request) ----------------
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<ISaleService, SaleService>();
        services.AddScoped<IReservationService, ReservationService>();

        // PLACEHOLDER only: we will replace this with the WebApi header-reading adapter.
        services.AddScoped<ITenantProvider, NullTenantProvider>();
        // ---------------- Validators (one strategy per request type) ----------------
        services.AddScoped<IValidator<RegisterRequest>, RegisterRequestValidator>();
        services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
        services.AddScoped<IValidator<CreateCustomerRequest>, CreateCustomerRequestValidator>();
        services.AddScoped<IValidator<CreateProductRequest>, CreateProductRequestValidator>();
        services.AddScoped<IValidator<ChangePriceRequest>, ChangePriceRequestValidator>();
        services.AddScoped<IValidator<RestockRequest>, RestockRequestValidator>();
        services.AddScoped<IValidator<CreateOrderRequest>, CreateOrderRequestValidator>();
        services.AddScoped<IValidator<AddOrderLineRequest>, AddOrderLineRequestValidator>();
        services.AddScoped<IValidator<ChangeOrderLineQuantityRequest>, ChangeOrderLineQuantityRequestValidator>();
        services.AddScoped<IValidator<ApplyDiscountRequest>, ApplyDiscountRequestValidator>();
        services.AddScoped<IValidator<CreateSaleRequest>, CreateSaleRequestValidator>();
        services.AddScoped<IValidator<ReserveRequest>, ReserveRequestValidator>();

        // services.AddScoped<IValidator<ReserveRequest>, ReserveRequestValidator>();

        // ---------------- Domain services ----------------
        // Singleton, not scoped: both are stateless rules, so the shorter lifetime costs nothing and
        // removes a per-request allocation. This is also the lifetime the pricing POLICY must have —
        // a scoped policy inside a singleton service would be a captive-dependency bug.
        services.AddSingleton<StockAvailabilityService>();
        services.AddSingleton<OrderPricingService>();

        AddDomainEventHandlers(services);

        return services;
    }

    private static void AddPricingStrategy(IServiceCollection services, PricingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        IPricingPolicy basePolicy = options.Strategy switch
        {
            PricingStrategy.Volume => new VolumeDiscountPricingPolicy(options.DiscountRate, options.ThresholdAmount),
            PricingStrategy.None => new NoDiscountPricingPolicy(),
            _ => throw new InvalidOperationException($"Unknown pricing strategy '{options.Strategy}'.")
        };

        // The campaign rule is ALWAYS in the composite: its answer is zero outside every window, so
        // registering it unconditionally costs nothing and keeps "does promo beat volume?" a test of
        // precedence, not of wiring. Composition root stays the only place the switch lives.
        services.AddSingleton<IPricingPolicy>(new PricingPolicyComposite(basePolicy, new BlackFridayPricingPolicy()));

        // services.AddSingleton(basePolicy);
    }

    private static void AddDomainEventHandlers(IServiceCollection services)
    {
        // Subscriptions are declared here as closed generic registrations, and the dispatcher resolves
        // IEnumerable<IDomainEventHandler<TEvent>> at run time. Two registrations for OrderPlaced is the
        // whole fan-out mechanism: one fact, two independent subscribers, neither knowing the other exists.
        services.AddScoped<IDomainEventHandler<OrderPlaced>, NotifyCustomerOnOrderPlacedHandler>();
        services.AddScoped<IDomainEventHandler<OrderPlaced>, AuditOrderLifecycleHandler>();
        services.AddScoped<IDomainEventHandler<OrderShipped>, AuditOrderLifecycleHandler>();
        services.AddScoped<IDomainEventHandler<OrderCompleted>, AuditOrderLifecycleHandler>();
        services.AddScoped<IDomainEventHandler<OrderCancelled>, RecordOrderCancellationHandler>();
    }
}
