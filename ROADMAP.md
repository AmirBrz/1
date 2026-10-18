# Black Friday Sale Reservation Service — Roadmap

Simple design, no extra features. Every step serves one of the four graded concerns.

Source of requirements: `black_friday_code_challenge_english.md`

**The four concerns:**

1. **Never oversell** — atomic inventory decrement (§2, line 38)
2. **Never lose a job** — background pipeline + retry + DLQ (§2, lines 39–40)
3. **Fast reads under burst** — cache + documented staleness (§3, lines 44–46)
4. **Tenant isolation** — namespaced data, queues, cache, errors (§4, line 50)

---

## 1. Compatibility check (requirement → step)

| Brief | Requirement | Step | Status after plan |
|---|---|---|---|
| §1 L32 | Create sale event: SKU, quantity, discounted price, start, end | 6, 12 | Covered |
| §1 L33 | Retrieve state: remaining inventory, active/inactive, tenant context | 6, 12, 18 | Covered |
| §2 L37 | Accept reservations while event is active | 10, 6 (`IsOn`) | Covered |
| §2 L38 | **Atomic** check + decrement, no race, no oversell | 5, 15 | Covered (DB-level) |
| §2 L39 | Background pipeline: order, warehouse, notification — real infra, stubbed logic | 17, 18 | Covered |
| §2 L40 | Retry + DLQ, nothing silently discarded | 17, 18 | Covered |
| §2 L21 | Failed job must not disappear without a trace | 3 (`Release`), 17 | Covered |
| §3 L44 | Sale status / metadata / tenant config read often | 13 | Covered |
| §3 L45 | Do not read primary DB every request | 13 | Covered (Redis) |
| §3 L46 | Document consistency vs staleness trade-off | 13, 23 (ADR-3) | Covered |
| §4 L50 | Namespaced storage, pipelines, intermediate layers | 1, 4, 13, 16, 17, 19 | Covered |
| TC L56 | .NET 8 or later | — | net9.0 OK |
| TC L57 | SQL Server in Docker Compose | 14, 20 | Covered |
| TC L58 | One command `docker-compose up`, no manual steps | 20, 21 | Covered |
| TC L59 | REST + Swagger | 18, existing Swagger | Covered |
| TC L60 | At least 1 meaningful concurrency test | 15, 22 | Covered |
| TC L62 | Extra components justified in an ADR | 23 | Covered (Redis = ADR-3) |
| Seed L68–78 | 2 tenants, 3 states each, 10 SKUs, 50–200 units, idempotent | 21 | Covered |
| D L86 | Runnable code | all | Covered |
| D L87–91 | README: run steps, curl, architecture, assumptions, limitations | 23 | Covered |
| D L92 | At least 1 ADR with context/alternatives/decision/consequences | 23 | 4 ADRs |
| D L93 | Evidence under concurrent load (logs/output) | 15, 22 | Covered |

**No requirement is left open.** Pricing/promotion is not a graded requirement — it is a small, natural fit for the existing code.

---

## 2. Scope rules

Keep it simple. Build only what the table above needs.

**Do NOT build:** reservation expiry timers · payment · rate limiting · schema-per-tenant · a `Tenant` entity · MediatR/CQRS · Kafka/RabbitMQ/Hangfire · cache key versioning · metrics dashboards · new UI · refactor of `Customer` or document numbers.

**Add no new infrastructure component without an ADR** (brief line 62). The plan adds only **Redis**, which is already in the project, plus **SQL Server** which the brief requires.

---

## 3. Domain first

### Step 1 — `Common/IMultiTenant.cs` (started)

One property: `Guid TenantId { get; }`.

*Why:* one marker interface = one place to apply the EF global filter later. No `Tenant` entity needed.

### Step 2 — `Common/` reuse

Use the existing `AggregateRoot`, `Entity`, `Guard`, `Result`, `ValueObject`. Do not add new base classes.

### Step 3 — `Entities/SaleEvent.cs` (new aggregate root)

Fields: `TenantId`, `Sku`, `TotalUnits`, `RemainingUnits`, `OfferPrice (Money)`, `StartsAt`, `EndsAt`.

Methods:

- `static Create(...)` — validates end > start, units > 0, price > 0
- `bool IsOn(DateTimeOffset now)` — **the only place** the window time is checked
- `Result TryReserve(int qty, DateTimeOffset now)` — returns failure, never throws for "sold out"
- `Release(int qty)` — puts a unit back when a pipeline job is dead-lettered

*Why:* `SaleEvent` is the real subject of this challenge, not `Product` and not `Order`.
`Release` is two lines and closes brief line 21 — a dead job must not leave a unit permanently locked.

**Critical decision:** `SaleEvent.RemainingUnits` is a **sale allocation**, separate from `Product.StockQuantity` (warehouse). The brief (line 20) says warehouse deduction is a *downstream* pipeline step. Do not reuse `Product.StockQuantity` as the reservation counter.

### Step 4 — `Entities/Reservation.cs`

Flat entity implementing `IMultiTenant`: `SaleEventId`, `Sku`, `Quantity`, `CustomerRef`, `CreatedAt`. No behavior, no aggregate.

*Why:* proof of who reserved what. Keep it boring.

### Step 5 — `Contracts/ISaleEventRepository.cs`

Four methods only:

- `GetAsync(Guid id, CancellationToken ct)`
- `ListActiveAsync(Guid tenantId, DateTimeOffset now, CancellationToken ct)`
- `AddAsync(SaleEvent, CancellationToken ct)`
- `Task<bool> TryReserveAsync(Guid saleEventId, int quantity, CancellationToken ct)` — the important one

`false` = not enough units. This forces the decision **out of memory and into the database**.

*Also:* `AddAsync(Reservation, ...)` may live on the same interface to avoid a second file.

### Step 6 — `Enums/SaleEventState.cs` + exceptions

Enum: `Upcoming, Active, Expired` — **computed from `IsOn(now)`, never stored** (a stored "state" column goes stale the second the clock moves).

Exceptions: `SaleNotActiveException`, `SaleSoldOutException`.

**Gate:** `dotnet build src/Samian.Domain` is clean and `Samian.Domain.csproj` still has **zero** package and project references.

---

## 4. Application

### Step 7 — Add `IMultiTenant` to existing entities

`Product`, `Order`, `Customer` get `TenantId`.

*Why:* isolation must cover old data too, or one tenant sees another's catalogue.

### Step 8 — `Features/Sales/` vertical slice

`CreateSaleRequest` + validator, `SaleStatusResponse` (read model: `RemainingUnits`, `State`, `TenantId`, `OfferAmount`, `OfferCurrency`), `ISaleService` (`CreateAsync`, `GetStatusAsync`).

Validators: end > start, quantity in 1..1000, one active sale per SKU per tenant.

### Step 9 — Pricing port: widen `IPricingPolicy`

Change the method to take one `PricingContext` record: `Subtotal`, `Lines`, `Now`, `TenantId`.

Keep it **synchronous and pure**. The comment at `IPricingPolicy.cs:10` already states the rule: *"if a rule ever needs data from outside, the DATA is passed in as a parameter."* This is that moment, not a design break.

The **use case** loads the active offer and passes it in. The policy never touches a repository, and **never touches inventory**.

*Why:* pricing decides the number; `SaleEvent` decides whether a unit exists. Merging them breaks the atomicity guarantee.

### Step 10 — `Features/Reservations/` vertical slice

`ReserveRequest(SaleEventId, Quantity, CustomerRef)` + validator, `ReservationResponse`, `IReservationService`, `ReservationService`.

Flow inside `ReservationService.ReserveAsync`:

1. `saleEvent.IsOn(now)` — reject with **409** if not active
2. resolve offer → compute price
3. `TryReserveAsync(...)` — **409 sold out** if false
4. stage `Reservation` + **outbox job row** in the **same transaction**
5. `SaveChangesAsync`
6. return immediately

*Why:* the brief (line 20) requires the customer answer to be instant while fulfilment happens out of band.

### Step 11 — Pricing: add the promotion strategy

- `BlackFridayPricingPolicy : IPricingPolicy` — discount only when `Now` is inside the active window
- `PricingPolicyComposite` — picks the **largest single discount and stops** (no stacking)
- Keep `NoDiscountPricingPolicy` and `VolumeDiscountPricingPolicy` as the other two strategies

Three strategies in one composite = the Strategy pattern with a real, testable precedence rule.

*Why non-stacking:* the simplest rule you can defend and test in one line. Record it as an assumption (brief line 90).

**Critical placement:** the promotion changes the **`OrderLine.UnitPrice` snapshot**, not `Order.Discount`.

*Why:* `Order.Discount` is per-order and `ApplyDiscount` **replaces** it (AGENTS.md:34). Using it would (a) discount items that are not in the sale, (b) let two rules silently delete each other, and (c) lose the price of record an invoice must show. An offer price is a **price**, not a concession. Add `SaleEventId?` to `OrderLine`.

### Step 12 — Cache keys per tenant

Rewrite `CacheKeys` so every key carries the tenant:

- `tenant:{t}:sale:{id}:status` — TTL **5 seconds** (volatile: units change every request)
- `tenant:{t}:sale:{id}:metadata` — TTL **5 minutes** (stable)
- `tenant:{t}:product:{id}` — TTL 5 minutes

Two TTLs, not one. *Why:* remaining units and sale metadata have different volatility, so they need different staleness budgets.

**Rule to write in the README:** cached values are **display only**. The reservation always recomputes price and stock from the database. A cache must be allowed to be stale, never allowed to be the source of truth.

---

## 5. Infrastructure

### Step 13 — Retarget SQL Server

`Microsoft.EntityFrameworkCore.SqlServer` **9.0.8** (verified present in the offline cache `F:\.nuget\packages`), `UseSqlServer`, connection string with `TrustServerCertificate=True`. Add `Microsoft.EntityFrameworkCore.Design` 9.0.8 for migrations.

### Step 14 — Switch `EnsureCreated` → migrations

The old comment in `DatabaseInitializer.cs` says design-time tooling was unavailable. It is available now. Migrations also give a deterministic seed path the brief accepts (line 68).

### Step 15 — Atomic decrement (the graded core)

`TryReserveAsync` = **one** SQL statement:

```sql
UPDATE SaleEvents
SET RemainingUnits = RemainingUnits - @qty
WHERE Id = @id AND TenantId = @tenantId AND RemainingUnits >= @qty
```

`rowsAffected == 1` → success. `0` → sold out.

*Why this and nothing else:* the current `StockAvailabilityService.DeductStock` reads stock into memory, checks it, then writes. 500 concurrent requests all read `200`, all pass the check, and you sell 500. A single conditional `UPDATE` is the only form that is also correct across **multiple app instances** (brief line 135).

**Write the concurrency test BEFORE this step** so you can prove it failed and then passed. That is deliverable #4 evidence.

### Step 16 — Global tenant filter

In `OnModelCreating`, for each `IMultiTenant` type: `HasQueryFilter(e => e.TenantId == _tenantId)`. Composite indexes starting with `TenantId`.

*Why:* isolation enforced once in the DbContext beats 20 manual `Where` clauses people forget.

### Step 17 — Outbox table + worker

One table `OutboxJob`: `Id`, `TenantId`, `SaleEventId`, `ReservationId`, `Payload`, `Status` (`Pending/Processing/Done/Dead`), `Attempts`, `NextAt`, `Error`.

- Insert **in the same `SaveChangesAsync` transaction** as the reservation decrement
- `OutboxWorker : BackgroundService` (no new package; it is in the shared framework)
- Poll: `Status='Pending' AND NextAt <= sysutcdatetime()`
- **Claim atomically:** an `UPDATE ... OUTPUT` that flips exactly one row to `Processing` (otherwise two workers process the same job twice)
- Steps (stubbed logic is fine — the brief allows it): create order → sync warehouse → send notification. Reuse the existing `IEmailSender`/`FileEmailSender`
- Failure: `Attempts++`, `NextAt = now + backoff(Attempts)`, back to `Pending`
- `Attempts >= 5` → `Status='Dead'` = **the DLQ**

**Same transaction is not optional:** a separate commit rebuilds the exact bug from their incident story (job accepted, then lost).

Keep `OutboxJobRow` in **Infrastructure only, not Domain** — it is storage machinery. The same call was already made for `DocumentSequenceRow` (`SamianDbContext.cs:30-32`).

### Step 18 — Endpoints

- `POST /api/sales` — create sale event
- `GET /api/sales/{id}/status` — remaining units + state + tenant
- `POST /api/reservations` — reserve
- `GET /api/jobs?status=Dead` — the DLQ view = "observable pipeline"

Controllers stay thin: one Application interface each (existing AGENTS.md:13 rule).

---

## 6. WebApi

### Step 19 — Tenant resolution

Middleware reads `X-Tenant-Id`, validates it, stores it in a scoped `ITenantProvider` the DbContext reads. Missing or unknown tenant → **400**, and **no query runs at all**.

*Why fail-fast:* a forgotten tenant filter must not quietly return another tenant's rows.

`ITenantProvider` lives in **Application** (a use-case concern), the HTTP implementation in **WebApi** — same split already used for `ICurrentUser`.

---

## 7. Docker

### Step 20 — `docker-compose.yml`

Three services: `api`, `mssql` (`mcr.microsoft.com/mssql/server`), `redis`.

- SQL Server needs a **healthcheck** (`sqlcmd ... SELECT 1`) and `api` needs `depends_on: condition: service_healthy`
- Without this, the API crashes before the database is ready — the number one reason a reviewer's `docker-compose up` fails
- Migration + seed run at API startup (already wired in `Program.cs:72`), so no manual step (brief line 58)

### Step 21 — Seed data (exact brief numbers)

| Requirement | Seed |
|---|---|
| 2 tenants | `tenant-electronics`, `tenant-fashion` — **fixed GUIDs** |
| 3 sale events each, different states | one **active** (past → future), one **upcoming** (future), one **expired** (past → past) |
| 10 SKUs across tenants | realistic names + prices |
| 50–200 units | e.g. 100 and 150 on the active events, for quick load tests |
| Deterministic + idempotent | guard each seed with an existence check; `down && up` twice must not duplicate rows |

**Warning:** a *fixed* timestamp for the active event becomes expired tomorrow, and the reviewer sees nothing active. Use relative windows for active/upcoming (`now - 1h → now + 1d`), a fixed past window for expired, and note the choice in the README.

---

## 8. Tests

### Step 22 — Three tests, in this order

1. **Concurrency (required, graded).** 200 units, 500 parallel reservations → **exactly 200 succeed, 300 rejected, `RemainingUnits == 0`, never negative.** Use `WebApplicationFactory` + `Task.WhenAll`. Run it against SQL Server, not only in-memory.
2. **Tenant isolation.** Sell tenant A to zero, assert tenant B's counters and cache keys are untouched; assert A's sale id returns not-found under B's tenant header.
3. **Pricing + clock.** 8:59:59 no promo → 9:00:00 promo → 9:30:00 no promo; plus "promotion wins over volume, they do not stack"; plus the existing `discount > subtotal` guard stays.

**Evidence to commit:** test output showing red before step 15 and green after, plus `scripts/load-test.ps1` (500 parallel `curl`) writing real logs. The brief accepts a Bash/curl script (line 60) — logs beat claims.

---

## 9. Docs (graded — budget real time)

### Step 23 — README + 4 ADRs

**README.md** must contain: run instructions · curl examples · architecture overview (one ASCII or Mermaid diagram) · assumptions · known limitations.

**ADR format** (brief line 92): Context → Alternatives considered → Decision → Consequences. **Write the consequences honestly, including the bad ones.** That is what "reasoned, not added without consideration" means.

| ADR | Decision | Alternatives to show |
|---|---|---|
| ADR-1 Atomic reservation | Conditional `UPDATE` + affected rows | `RowVersion` + retry (wasteful under contention) · Redis/Lua counter (fast, but needs DB reconciliation — the scale path) · in-process lock (unsafe across instances) |
| ADR-2 Job handoff | DB transactional outbox + polling worker | broker/RabbitMQ (new infra, needs its own ADR) · in-memory `Channel<T>` (**rejected: dies with the process** — the exact incident failure) · `DomainEventDispatcher` as-is (**rejected: synchronous, lost on crash**) |
| ADR-3 Read strategy | Two-tier Redis + per-read TTLs, absolute expiration | DB-per-request (banned by line 45) · memory only (no sharing) · sliding TTL (**rejected: a hot key can expire forever**) |
| ADR-4 Offer price | Line snapshot + `SaleEventId` | `Order.Discount` (rejected: not per-SKU, replaces, loses price of record) |

Redis is the only added component → ADR-3 also satisfies brief line 62.

---

## 10. Time budget (~5 hours of coding)

| Phase | Steps | Time |
|---|---|---|
| Domain | 1–6 | 45 min |
| Application | 7–12 | 40 min |
| Infrastructure | 13–18 | 80 min |
| WebApi | 19 | 20 min |
| Docker + seed | 20–21 | 60 min |
| Tests | 22 | 45 min |
| README + ADRs | 23 | 60 min |

**Get `docker-compose up` working at step 14, not at the end.** Docker issues always take longer than expected, and the brief makes the app unusable without it.

---

## 11. Acceptance checklist

Before submitting:

- [ ] Fresh clone → `docker-compose up` → Swagger opens, seeded sales are visible
- [ ] Same command works a second time (seed idempotent)
- [ ] 500 parallel reservations on 200 units → exactly 200 accepted (log committed)
- [ ] Two app instances against one SQL Server → still no oversell
- [ ] Kill the worker mid-job → job returns to `Pending` and later completes
- [ ] Force a step to fail 5× → `Attempts` climbs with backoff → row lands in `Dead` → visible at `GET /api/jobs?status=Dead`
- [ ] Tenant A sold out, tenant B unaffected
- [ ] No `IQueryable` in any Domain/Application port, no package reference in Domain
- [ ] README, 4 ADRs, concurrency test output all committed
- [ ] `Directory.Build.props` and every csproj agree on one target framework

---

## 12. Assumptions to state in the README

1. Tenant identified by `X-Tenant-Id` header
2. Sale allocation is separate from warehouse stock; warehouse sync is a pipeline step
3. A reservation holds a unit with no payment and **no expiry** (would need a release timer — listed under limitations)
4. Promotion and volume discount do **not** stack; the larger single discount wins
5. Displayed prices may lag the authoritative price by the documented TTL; reservations never use a cached value

---

## 13. Key file map

| Area | Files |
|---|---|
| Domain | `Common/IMultiTenant.cs`, `Entities/SaleEvent.cs`, `Entities/Reservation.cs`, `Enums/SaleEventState.cs`, `Contracts/ISaleEventRepository.cs`, `Exceptions/` |
| Application | `Features/Sales/`, `Features/Reservations/`, `Contracts/IPricingPolicy.cs`, `Contracts/ITenantProvider.cs`, `Common/CacheKeys.cs` |
| Domain services | `Services/PricingPolicies.cs` (+`BlackFridayPricingPolicy`, `PricingPolicyComposite`) |
| Infrastructure | `Persistence/SamianDbContext.cs`, `Persistence/Repositories/SaleEventRepository.cs`, `Persistence/Configurations/`, `Outbox/OutboxJobRow.cs`, `Outbox/OutboxWorker.cs`, `Caching/RedisCacheService.cs`, `Persistence/DatabaseInitializer.cs` → seeder |
| WebApi | `Middleware/TenantMiddleware.cs`, `Controllers/SalesController.cs`, `Controllers/ReservationsController.cs`, `Controllers/JobsController.cs`, `appsettings.json` |
| Root | `docker-compose.yml`, `Dockerfile`, `README.md`, `docs/adr/0001–0004`, `scripts/load-test.ps1`, `tests/Samian.Tests/` |
