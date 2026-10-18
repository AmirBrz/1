using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Samian.Application;
using Samian.Application.Configuration;
using Samian.Application.Contracts;
using Samian.Infrastructure;
using Samian.Infrastructure.Outbox;
using Samian.Infrastructure.Persistence;
using Samian.WebApi.Identity;
using Samian.WebApi.Middleware;
using Samian.WebApi.Tenancy;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// COMPOSITION ROOT. This file is the only place that knows every layer exists:
//   Domain      -> knows nothing
//   Application -> knows Domain
//   Infrastructure -> knows Application (and therefore Domain), implements its ports
//   WebApi      -> knows Application + Infrastructure, but only to WIRE them
// A controller receives IOrderService (Application). It never sees a repository, a DbContext or a
// DocumentNumber converter, which is the practical test that dependency inversion was honoured.
// ---------------------------------------------------------------------------

// Bound in the host because the host is the only layer allowed to read appsettings.
var pricingOptions = builder.Configuration.GetSection(PricingOptions.SectionName).Get<PricingOptions>()
                     ?? new PricingOptions();

builder.Services.AddApplication(pricingOptions);
builder.Services.AddInfrastructure(builder.Configuration);

// The fulfilment pipeline (Step 17): a singleton BackgroundService that creates its own DI scopes per
// poll, so registering the HOSTED service is the only thing the host says about it — the queue,
// inspector and worker internals stay inside Infrastructure (onion rule: the host wires, never implements).
builder.Services.AddHostedService<OutboxWorker>();

// ICurrentUser is implemented HERE, not in Infrastructure: reading claims is an HTTP concern, and the
// host is as much an adapter as the database is. Infrastructure would need an ASP.NET reference to do
// it, which is exactly the kind of leak the onion is arranged to prevent.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

// Same adapter logic as ICurrentUser: the TENANT port is implemented here because reading and
// validating X-Tenant-Id is an HTTP concern. This registration replaced the NullTenantProvider
// placeholder — Application no longer registers a tenant source at all.
builder.Services.AddScoped<ITenantProvider, HttpContextTenantProvider>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSamianSwagger();
builder.Services.AddSamianJwtAuthentication(builder.Configuration);

var app = builder.Build();

// FIRST in the pipeline: it must be able to observe an exception thrown by authentication, a controller
// or the unit of work, and turn it into one Problem Details shape. Registered last, it would only catch
// what the innermost middleware threw.
app.UseSamianErrorHandling();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // Dev bypasses the redirect on purpose: the smoke-test profile listens on http://localhost:5000 and
    // an unconditional redirect there produces a 307 loop with no certificate to complete it.
    app.UseHttpsRedirection();
}

// Authentication before authorization before the endpoints. Skipping UseAuthentication is a classic
// "why is User.Identity null" hour of debugging.
app.UseAuthentication();
app.UseAuthorization();

// AFTER auth (an unauthenticated call to a tenant route should answer 401 before 400), BEFORE
// MapControllers — so a missing or unknown X-Tenant-Id produces 400 and NO query runs at all.
app.UseSamianTenantResolution();

app.MapControllers();

// Migrate-and-seed on start (DatabaseInitializer): the brief's "docker-compose up, no manual
// steps" requirement means schema AND data come up with the process. MigrateAsync is idempotent;
// note that a multi-instance deployment must still run this once (a migration lock, or an
// entrypoint job), not per node.
await app.Services.InitializeAsync(CancellationToken.None);

app.Run();

// ---------------------------------------------------------------------------
// Host plumbing. A type declared AFTER the top-level statements is legal C# and keeps the whole
// request pipeline readable in one screen. (Extension methods must sit in a static, non-generic class,
// CS1106 — which is why these are extensions rather than local functions.)
// ---------------------------------------------------------------------------
static class SamianHostSetup
{
    public static IServiceCollection AddSamianJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // Read the SAME section Infrastructure reads for signing. Two consumers, one source of truth:
        // if the issuer or the key drifted between them, every token the API issued would fail its own
        // validation, and that is a startup bug, not a runtime one.
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
                  ?? throw new InvalidOperationException($"Configuration section '{JwtOptions.SectionName}' is missing.");

        // Fail-fast instead of IDX10604 on the first login: HS256 needs at least 128 bits of key material,
        // and an empty SecretKey in appsettings.json means "provided by the environment", not "sign with
        // nothing". A deployment that forgot the variable should crash on boot, not issue weak tokens.
        if (jwt.SecretKey.Length < 32)
            throw new InvalidOperationException(
                $"{JwtOptions.SectionName}:{nameof(JwtOptions.SecretKey)} must be configured and at least 32 characters long.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey)),
                    ValidateLifetime = true,

                    // The 5-minute default skew is a convenience for badly synchronised servers. An ERP
                    // document API with a 2-hour token life gains nothing from tolerance, and every
                    // accepted extra second is a second a revoked-but-still-valid token can be used.
                    ClockSkew = TimeSpan.Zero,

                    // No claim-type rewriting: keep whatever the generator put in the token.
                    NameClaimType = System.Security.Claims.ClaimTypes.Name,
                    RoleClaimType = System.Security.Claims.ClaimTypes.Role,
                };
            });

        // [Authorize] and [Authorize(Roles = ...)] resolve through this; without it the attributes are
        // silently ignored, which is the most dangerous misconfiguration an API can have.
        services.AddAuthorization();

        return services;
    }

    public static IApplicationBuilder UseSamianTenantResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantMiddleware>();

    public static IServiceCollection AddSamianSwagger(this IServiceCollection services)
    {
        const string bearerScheme = "Bearer";

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Samian ERP sample",
                Version = "v1",
                Description =
                    "Onion/DDD order-management sample. Sign in with the seeded accounts (admin / " +
                    "Admin#1234, salesclerk / Sales#1234), click Authorize, then walk an order from " +
                    "draft to delivered.",
            });

            // Without these two blocks Swagger still works — it just cannot authenticate, and every
            // protected endpoint looks broken to the person reviewing the project.
            options.AddSecurityDefinition(bearerScheme, new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "Paste the access_token from /api/auth/login here.",
            });

            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = bearerScheme },
                    },
                    Array.Empty<string>()
                },
            });
        });

        return services;
    }
}
