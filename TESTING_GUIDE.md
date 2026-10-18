# Samian — Testing Guide

This guide shows a tester how to use the app with simple HTTP calls.
It covers: **login**, a normal **order**, a Black Friday **reservation**, how the app
**locks units** so two users cannot take the last item, and how a reservation is
**released back** (canceled).

You can copy the commands. They are written for **PowerShell** (your terminal).
The app uses plain JSON, so you can also use Postman or `curl` if you prefer.

---

## 1. Before you start

Run the app. It needs SQL Server + Redis. The easy way:

```powershell
docker compose up --build
```

Or run the API only (if the database is already up):

```powershell
dotnet run --project src/Samian.WebApi
```

The API listens on: `http://localhost:5144`
Swagger (a web page where you can click the endpoints): `http://localhost:5144/swagger`

In all examples below we use a variable for the base address:

```powershell
$Base = "http://localhost:5144"
```

---

## 2. Accounts and the tenant

The app is multi-tenant. Each retailer is a **tenant**. The test data has two tenants,
but the seeded users belong to the first one.

| Item | Value |
|------|-------|
| Tenant id (Electronics) | `11111111-1111-1111-1111-111111111111` |
| Admin user | `admin` / `Admin#1234` (can write catalogue + sales + see jobs) |
| Clerk user | `salesclerk` / `Sales#1234` (can order and reserve) |

> Important: the tenant is part of the login. After you log in, the app knows your tenant
> from your token. You do **not** need to send a tenant header on other calls.

---

## 3. Login

```powershell
$login = @{
  userName = "admin"
  password = "Admin#1234"
  tenantId = "11111111-1111-1111-1111-111111111111"
} | ConvertTo-Json

$auth = Invoke-RestMethod -Uri "$Base/api/auth/login" -Method Post -Body $login -ContentType "application/json"
$auth
```

The answer looks like this:

```json
{
  "token": "eyJhbGciOi...",
  "userName": "admin",
  "role": "Admin",
  "tenantId": "11111111-1111-1111-1111-111111111111",
  "expiresAtUtc": "..."
}
```

Save the token. Every call after this uses it:

```powershell
$Headers = @{ Authorization = "Bearer $($auth.token)" }
```

**To test the lock later, also log in a second user in another terminal:**

```powershell
$login2 = @{
  userName = "salesclerk"
  password = "Sales#1234"
  tenantId = "11111111-1111-1111-1111-111111111111"
} | ConvertTo-Json

$auth2 = Invoke-RestMethod -Uri "$Base/api/auth/login" -Method Post -Body $login2 -ContentType "application/json"
$Headers2 = @{ Authorization = "Bearer $($auth2.token)" }
```

> Wrong password, or a user that does not exist in that tenant → HTTP **401**.
> The app gives the same 401 for both, so it does not reveal which names exist.

---

## 4. Place a normal order

A normal order uses the **warehouse stock** (`Product.StockQuantity`).
Full flow: create product → create customer → draft order → add line → place → confirm → ship.

### 4.1 Create a product (Admin only)

```powershell
$product = @{
  name          = "Test Laptop"
  sku           = "TEST-LAPTOP"
  priceAmount   = 5000000
  currency      = "IRR"
  stockQuantity = 3
} | ConvertTo-Json

$prod = Invoke-RestMethod -Uri "$Base/api/products" -Method Post -Headers $Headers -Body $product -ContentType "application/json"
$prod
```

Remember the `id` in the answer. The stock is **3**.

### 4.2 Create a customer

```powershell
$customer = @{
  name    = "Test Buyer"
  address = "Tehran, Test St 1"
  email   = "buyer@test.example"
} | ConvertTo-Json

$cust = Invoke-RestMethod -Uri "$Base/api/customers" -Method Post -Headers $Headers -Body $customer -ContentType "application/json"
$cust
```

### 4.3 Create a draft order

The currency must be the same as the product (`IRR`).

```powershell
$order = @{
  customerId = $cust.id
  currency   = "IRR"
} | ConvertTo-Json

$orderResp = Invoke-RestMethod -Uri "$Base/api/orders" -Method Post -Headers $Headers -Body $order -ContentType "application/json"
$orderResp
```

You now have a `Draft` order with a document number like `ORD-000123`.

### 4.4 Add a line to the order

```powershell
$line = @{
  productId = $prod.id
  quantity  = 2
} | ConvertTo-Json

$orderResp = Invoke-RestMethod -Uri "$Base/api/orders/$($orderResp.id)/lines" -Method Post -Headers $Headers -Body $line -ContentType "application/json"
$orderResp
```

### 4.5 Place, then confirm, then ship

```powershell
Invoke-RestMethod -Uri "$Base/api/orders/$($orderResp.id)/place"   -Method Post -Headers $Headers | Out-Null
Invoke-RestMethod -Uri "$Base/api/orders/$($orderResp.id)/confirm" -Method Post -Headers $Headers | Out-Null
$shipped = Invoke-RestMethod -Uri "$Base/api/orders/$($orderResp.id)/ship" -Method Post -Headers $Headers
$shipped
```

Status moves like this: `Draft → Placed → Confirmed → Shipped`.

### 4.6 Check that the stock went down

Only **Ship** reduces real stock (2 units).

```powershell
$after = Invoke-RestMethod -Uri "$Base/api/products/$($prod.id)" -Method Get -Headers $Headers
$after
```

You should see `stockQuantity` = **1** (was 3, order shipped 2).

> Note on the normal order path: **Place** only checks that the stock is enough. It does not
> hold the units. The stock is taken at **Ship**. This is the classic ERP rule of this app.

---

## 5. Make a Black Friday reservation

A reservation is the fast sale path. It uses a **different number**: the sale allocation
(`SaleEvent.RemainingUnits`), **not** the warehouse stock.

### 5.1 Create a sale (Admin only)

Make the sale **active now**: start a few minutes ago, end tomorrow.

```powershell
$sale = @{
  sku         = "TEST-LAPTOP"
  totalUnits  = 5
  offerAmount = 4000000
  currency    = "IRR"
  startsAt    = (Get-Date).AddMinutes(-5).ToUniversalTime().ToString("o")
  endsAt      = (Get-Date).AddDays(1).ToUniversalTime().ToString("o")
} | ConvertTo-Json

$s = Invoke-RestMethod -Uri "$Base/api/sales" -Method Post -Headers $Headers -Body $sale -ContentType "application/json"
$s
```

You get `remainingUnits = 5` and `state = "Active"`.

You can see all active sales for your tenant:

```powershell
Invoke-RestMethod -Uri "$Base/api/sales/active" -Method Get -Headers $Headers
```

### 5.2 Reserve some units

```powershell
$reserve = @{
  saleEventId = $s.id
  quantity    = 3
  customerRef = "buyer-001"
} | ConvertTo-Json

$r = Invoke-RestMethod -Uri "$Base/api/reservations" -Method Post -Headers $Headers -Body $reserve -ContentType "application/json"
$r
```

Answer is HTTP **201** with the reservation. Now check the sale:

```powershell
Invoke-RestMethod -Uri "$Base/api/sales/$($s.id)/status" -Method Get -Headers $Headers
```

`remainingUnits` is now **2**.

---

## 6. Prove the lock: no overselling

This is the core of the challenge. When units are reserved, other users **cannot** take more
than what is left. The database does the check and the take in one atomic step, so two people
cannot both pass the same "is there enough?" test.

**Same sale, only 2 units left.** A second user tries to reserve 3.

```powershell
$reserve2 = @{
  saleEventId = $s.id
  quantity    = 3
  customerRef = "buyer-002"
} | ConvertTo-Json

try {
  Invoke-RestMethod -Uri "$Base/api/reservations" -Method Post -Headers $Headers2 -Body $reserve2 -ContentType "application/json"
} catch {
  $_.Exception.Response.StatusCode.value__   # prints the HTTP status
}
```

Result: HTTP **409** — "sold out, no units left to reserve." Nothing was taken.

Now the second user reserves only **2** (the real remaining amount) → it works:

```powershell
$reserve3 = @{
  saleEventId = $s.id
  quantity    = 2
  customerRef = "buyer-002"
} | ConvertTo-Json

Invoke-RestMethod -Uri "$Base/api/reservations" -Method Post -Headers $Headers2 -Body $reserve3 -ContentType "application/json"
Invoke-RestMethod -Uri "$Base/api/sales/$($s.id)/status" -Method Get -Headers $Headers   # remainingUnits = 0
```

After that, any further reservation returns **409**, because `remainingUnits = 0`.
**The product is fully locked** — 5 sold, 0 left, never 6.

### 6.1 The big concurrent burst (optional but powerful)

To see it under load, fire many reservations at the same time against a sale with, say, 150 units,
and count the answers. Exactly 150 get `201`, the rest get `409`. Example with PowerShell jobs
against a fresh active sale (`$sId`) using one token:

```powershell
$sId = "<active sale id>"
$h   = @{ Authorization = "Bearer $($auth.token)" }
$body = @{ saleEventId = $sId; quantity = 1; customerRef = "burst" } | ConvertTo-Json

$ok  = 0
$err = 0
1..200 | ForEach-Object -Parallel {
  try {
    Invoke-RestMethod -Uri "$using:Base/api/reservations" -Method Post -Headers $using:h -Body $using:body -ContentType "application/json" | Out-Null
  } catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 409) { $using:err } else { $using:err }
  }
} -ThrottleLimit 50
```

> If a script file exists in the repo (a load-test script), you can use that instead.
> The manual test above is enough to prove the rule on its own.

---

## 7. Important: there are TWO stock numbers

This is the point many testers miss. Please read it once.

| Name | Field | What it means | Who changes it |
|------|-------|---------------|----------------|
| Sale allocation | `SaleEvent.RemainingUnits` | Units allowed **in this sale** | **Reservation** changes it, atomically, instantly |
| Warehouse stock | `Product.StockQuantity` | Real items in the store | Normal **Order** at Ship; and later the background pipeline |

So:

- A **reservation** locks the **sale allocation** against other **reservations**. That is the true
  "no oversell" guarantee, and Section 6 proves it.
- A reservation does **not** instantly block a normal **order** for the same product, because the
  order reads a different number (`StockQuantity`). The order path is a separate, older flow.
- The reservation updates the warehouse stock **later**, out of band, through the background job
  (Section 8). This keeps the reservation answer fast, which the challenge asks for.

If you want to see the reservation also reduce the warehouse product, reserve a SKU that is also a
stocked product. The seeded product "65 inch 4K TV" has sku `65INCH-TV` and stock `300`, and there
is a seeded active sale for the same sku. After a reservation of `65INCH-TV`, wait a few seconds and
check the product stock — the background pipeline lowers it.

```powershell
# find the seeded active sale, reserve 1 of 65INCH-TV, then read the product list
Invoke-RestMethod -Uri "$Base/api/products?pageSize=50" -Method Get -Headers $Headers
```

---

## 8. How a reservation is canceled (released)

**Honest answer first:** there is **no button/endpoint for a shopper to cancel a reservation.**
Once you reserve, the units are gone from the sale.

The system gives units **back to the sale automatically** in one case only: when the background
job for that reservation **fails all its retries and dies** (goes to the Dead Letter queue). At
that moment the app releases the units, so a broken order does not lock stock forever.

You can trigger this on purpose. A `customerRef` that contains the word **`boom`** makes the first
pipeline step fail every time, on purpose.

```powershell
$boom = @{
  saleEventId = $s.id
  quantity    = 1
  customerRef = "boom-me"
} | ConvertTo-Json

Invoke-RestMethod -Uri "$Base/api/reservations" -Method Post -Headers $Headers -Body $boom -ContentType "application/json"
```

The reservation still returns **201** (the customer got an instant answer, and 1 unit is now held).
But the background job will keep failing. Watch it:

```powershell
# Pending / Processing jobs for your tenant
Invoke-RestMethod -Uri "$Base/api/jobs" -Method Get -Headers $Headers

# The dead-letter view (Admin only)
Invoke-RestMethod -Uri "$Base/api/jobs?status=Dead" -Method Get -Headers $Headers
```

The job retries with a growing wait (10s, then 20s, 40s, 80s). After **5 failed tries** it becomes
`Dead`. When it dies, the app returns the held unit to the sale. So after about 2-3 minutes:

```powershell
Invoke-RestMethod -Uri "$Base/api/sales/$($s.id)/status" -Method Get -Headers $Headers
```

You will see `remainingUnits` go **back up by 1** — that is the release / cancel effect.

Summary of cancellation:

- User cancel: **not available** in this build.
- Automatic release: happens when the job is exhausted and lands in `Dead` (use `boom` to force it).
- The units return to the sale, and you can see the job's error text in `GET /api/jobs?status=Dead`.

---

## 9. Endpoint quick reference

| What | Method + Route | Who | Body (main fields) |
|------|----------------|-----|--------------------|
| Login | `POST /api/auth/login` | Anyone | `userName, password, tenantId` |
| Register | `POST /api/auth/register` | Anyone | `userName, password, tenantId` |
| Create product | `POST /api/products` | Admin | `name, sku?, priceAmount, currency, stockQuantity` |
| List products | `GET /api/products?page=0&pageSize=20` | Any signed-in | — |
| Create customer | `POST /api/customers` | Any signed-in | `name, address, email?` |
| Create order (draft) | `POST /api/orders` | Any signed-in | `customerId, currency` |
| Add order line | `POST /api/orders/{id}/lines` | Any signed-in | `productId, quantity` |
| Place / Confirm / Ship / Deliver / Cancel | `POST /api/orders/{id}/place` (and `/confirm`, `/ship`, `/deliver`, `/cancel`) | Any signed-in | cancel: `reason` |
| Create sale | `POST /api/sales` | Admin | `sku, totalUnits, offerAmount, currency, startsAt, endsAt` |
| Sale status | `GET /api/sales/{id}/status` | Any signed-in | — |
| Active sales | `GET /api/sales/active` | Any signed-in | — |
| Reserve | `POST /api/reservations` | Any signed-in | `saleEventId, quantity, customerRef` |
| Jobs (pipeline / DLQ) | `GET /api/jobs?status=Dead` | Admin | — |

Status codes you will see:

- **200 / 201** — success.
- **400** — bad input, or a domain rule broken (validation message in the body).
- **401** — not signed in, or bad login.
- **403** — signed in but not allowed (for example a clerk creating a product).
- **404** — the id does not exist, or it belongs to another tenant (you cannot see it).
- **409** — a clash, for example "sold out" or a wrong order state.

---

## 10. Notes for the tester

- Always send `Authorization: Bearer <token>` after login. No tenant header is needed.
- All ids (order, product, sale) are GUIDs returned by the create calls — use them, do not invent.
- `currency` must match everywhere for one order (the seed uses `IRR`).
- A sale must be in window (`Active`) to accept reservations. If `state` is `Expired` or
  `Upcoming`, a reservation returns **409**.
- Reservations accept quantity **1 to 1000** only (a typo guard).
- Use `customerRef` containing `boom` to test the failure and release path.
