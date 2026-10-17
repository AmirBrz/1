# Samian — domain decisions

## Architecture
- Onion Architecture. `Domain` has zero project references and zero NuGet packages.
- Target framework: **net9.0** — retargeted from net8.0 because the only offline-cached
  `Microsoft.AspNetCore.Authentication.JwtBearer` (9.0.5) supports net9.0 only. One line per csproj
  plus `Directory.Build.props`; the installed runtime was verified before switching.
- `Contracts/` = ports (repository interfaces, `IClock`, `IDocumentNumberGenerator`, `IUnitOfWork`).
- `Services/` = domain services (cross-aggregate rules) and the concrete pricing policies.
- Ports never return `IQueryable`; lookups return nullable (`Order?`) for "not found".
- Auth abstractions (`IJwtTokenGenerator`, password hashing) belong to Application, NOT Domain.
  `IClock` belongs to Domain because time is business-sensitive.
- The WEB API is a composition root + adapters only: it implements `ICurrentUser` (claims reading is
  an HTTP concern), registers nothing itself beyond auth/swagger, and a controller may depend on
  exactly one thing — an Application service interface.
- Dependency direction proven by the csproj graph: Infrastructure→Application→Domain; WebApi→
  Application+Infrastructure (wiring only). If `Infrastructure` ever needs a `using` from WebApi,
  the onion is broken.

## Strategic
- One bounded context for now (Sales). Aggregates: `Order` (root, owns `OrderLine`), `Product`,
  `Customer`, `User`. Cross-aggregate references are BY ID ONLY — no navigation between aggregates.

## Tactical decisions (each one is enforced in code, not just written here)
- **Document number: NO year.** Format `ORD-000123` = 3-letter series prefix + 6-digit sequence.
  One counter per series; sequence capacity is validated so `Parse(From(...).Value)` always round-trips.
- **Currency: one currency per order, chosen at creation.** `Order.Create(customerId, currency = "IRR")`.
  IRR is the DEFAULT, not the only legal value. All lines of an order must share its currency.
- **Price snapshot:** `OrderLine.UnitPrice` is frozen at the time of ordering. `Order.Total` is
  computed from line snapshots, never from `Product.Price` — changing a price tomorrow must not
  rewrite yesterday's order.
- **Totals:** `Subtotal` = sum of line totals (derived, not stored). `Total = Subtotal - Discount`.
  Only `Order` writes `Total`; `Discount` is stored because it is a decision, not a computation.
- **Discount semantics:** applying a discount REPLACES the previous one (latest wins, not additive);
  `Money.Zero` removes it. A discount may equal the subtotal (100 % / free-of-charge) but never exceed it.
- **Pricing:** `OrderPricingService` composes an `IPricingPolicy` and returns `Result<Money>` — it
  validates the policy's answer; it never computes a total and never mutates the order.
- **Editability:** lines and discount change only while the order is a `Draft`.
- **State machine:** `Draft → Placed → Confirmed → Shipped → Delivered`. `Delivered` and `Cancelled`
  are terminal. `Pending` was REMOVED because nothing could ever reach it (unreachable state = dead code).
- **Cancellation:** allowed from `Draft`, `Placed`, `Confirmed` — never after shipment.
- **One line per product:** adding the same product again raises that line's quantity.
- **Failure style:** entities THROW (`DomainException` and its subclasses) because a broken invariant
  means the object must not exist. Domain SERVICES return `Result` because a failed check is a normal
  business outcome the caller reports as a 4xx.
- **Time:** no `DateTime.Now` in the domain. The application service reads `IClock.UtcNow` and passes
  the value into the aggregate method (`PlaceOrder(now)`), which stores it and stamps the event.
- **Stock:** `StockAvailabilityService.DeductStock` decrements `Product` aggregates; it is called by
  the application service BEFORE `ShipOrder`, in the same unit of work. `ShipOrder` cannot verify it
  without crossing the aggregate boundary, which is why the rule lives in the use case, not the entity.
- **Domain events:** raised inside the root, buffered on `AggregateRoot`, dispatched by Infrastructure
  after `SaveChangesAsync`, then cleared via `ClearDomainEvents()`.
- **Enum numbering** (`OrderStatus`, `DocumentSeries`) is persisted: never renumber an existing member,
  only append (that is why `Placed = 3` keeps the gap left by `Pending = 2`).

## Application-layer decisions
- **Vertical slices:** one folder per feature (`Features/Auth`, `Features/Products`, `Features/Orders`,
  `Features/Customers`); each file holds the request records, response records, validators, service
  interface and implementation together.
- **DTOs are records**, and responses are READ MODELS (flattened `Money` into amount + currency, enum
  as string) — aggregates never cross the API boundary.
- **Validation:** hand-rolled `IValidator<TRequest>` (strategy pattern, one per request type) with
  `[GeneratedRegex]` rules; a failed validation throws `AppValidationException` → 400 with per-field
  messages. Domain guards stay as the second, invariant-level line of defense.
- **Document number is taken at draft CREATION, not at placement** — a draft is already quotable, and
  moving the block to `PlaceAsync` is the one-line answer if the business ever changes its mind.
- **Caching is per use case:** `ProductService`/`OrderService` reads inject `ICacheService`
  (cache-aside over a two-tier L1 memory + L2 Redis adapter that degrades to L1 when Redis is down,
  proven live in the smoke run); `CustomerService` deliberately does NOT cache. Write paths delete
  the affected keys.
- **Domain event handlers live in Application** (`Events/OrderEventHandlers.cs`) and are dispatched
  in-process after `SaveChangesAsync` via a reflection-based dispatcher (no MediatR offline). A
  handler failure surfaces as a 500 — a production system would add a real outbox.
- **Pricing strategy is configuration:** `Pricing:Strategy` (`None`|`Volume`) + rate + threshold in
  appsettings, constructed once in `AddApplication`. The automatic-discount endpoint runs the policy.

## Infrastructure/host decisions
- **Persistence:** EF Core 9 + SQLite, `EnsureCreated` + dev-only seed at startup (`DatabaseInitializer`),
  NOT migrations — the honest trade-off is documented in that file; switch point is one method.
- **Money maps to ONE text column `"amount|currency"`** via a `ValueConverter` (the struct has fields,
  not properties, so owned types are unavailable); `DocumentNumber` maps to its own string with a
  `ValueComparer` so `Where(o => o.Number == n)` translates to SQL.
- **SQLite quirks handled:** Guid keys are client-assigned (`Order.Create` does `Guid.NewGuid()`), and
  `ORDER BY` on `DateTimeOffset` is not supported — those sorts run in memory after materialization
  (see `OrderRepository` comments).
- **Security:** PBKDF2-SHA512 (BCL only, parameters embedded in the hash string), JWT HS256 with
  `ClockSkew = TimeSpan.Zero`, secret ≥ 32 chars validated at startup (fail fast, never sign weakly).
  Login answers the identical 401 for unknown user and wrong password (no name enumeration).
- **Errors are one funnel:** `ExceptionHandlerMiddleware` (first in the pipeline) maps exception types
  to Problem Details (RFC 9457): 400 validation/domain, 401 credentials, 403 forbidden, 404 missing,
  409 state/stock/conflict, 500 everything else. Controllers contain no try/catch.
- **Roles:** `Admin` writes the catalogue, everyone authenticated walks orders (`[Authorize(Roles)]` on
  endpoints; the seeded accounts are `admin / Admin#1234` and `salesclerk / Sales#1234`).
- **`IEmailSender` is a file outbox** (`%TEMP%\samian-outbox\*.eml`) so event-handler side effects are
  observable; swapping in SMTP is a new adapter, zero use-case changes.

## Not done yet (deliberately deferred)
- Domain unit tests and architecture tests — the user chose to skip tests in this phase.
  `smoke.ps1` at the repo root is the current end-to-end proof: 19 checks, all passing against a
  fresh database (auth, RBAC, catalogue, full order lifecycle, stock deduction, event email,
  negative 401/403/409/400 paths).
- Migrations, real outbox, refresh tokens, pagination envelopes.
