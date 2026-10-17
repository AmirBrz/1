# End-to-end smoke walk of the Samian API.
# Run the WebApi first (dotnet run --project src\Samian.WebApi --launch-profile http), then:
#   pwsh -File smoke.ps1
#expects 19 PASS. Delete src\Samian.WebApi\samian.db* between runs for identical numbers.
$ErrorActionPreference = 'Stop'
$base = 'http://localhost:5144'
$pass = 0; $fail = 0

function Check($name, $ok, $detail = '') {
    if ($ok) { $script:pass++; Write-Host "PASS  $name $detail" -ForegroundColor Green }
    else     { $script:fail++; Write-Host "FAIL  $name $detail" -ForegroundColor Red }
}

# 1. Login as admin
$login = Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' `
    -Body (@{ userName = 'admin'; password = 'Admin#1234' } | ConvertTo-Json)
Check 'login admin returns token' ($login.token.Length -gt 20) "(role=$($login.role))"
$H = @{ Authorization = "Bearer $($login.token)" }

# 2. Anonymous product list must be 401
try { Invoke-RestMethod -Uri "$base/api/products" | Out-Null; Check 'anonymous list 401' $false }
catch { Check 'anonymous list 401' ($_.Exception.Response.StatusCode -eq 401) }

# 3. Cached list + single get
$list = Invoke-RestMethod -Uri "$base/api/products?page=0&pageSize=20" -Headers $H
Check 'seeded catalogue has 4 products' ($list.Count -eq 4)
$list2 = Invoke-RestMethod -Uri "$base/api/products?page=0&pageSize=20" -Headers $H
Check 'second list call served (cache-safe equality)' (($list2 | ConvertTo-Json) -eq ($list | ConvertTo-Json))
$kb = $list | Where-Object Name -eq 'Mechanical Keyboard'

# 4. Clerk cannot create products (403)
$clerk = Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' `
    -Body (@{ userName = 'salesclerk'; password = 'Sales#1234' } | ConvertTo-Json)
$HC = @{ Authorization = "Bearer $($clerk.token)" }
try {
    Invoke-RestMethod -Method Post -Uri "$base/api/products" -Headers $HC -ContentType 'application/json' `
        -Body (@{ name = 'Hack Try'; priceAmount = 1; currency = 'IRR' } | ConvertTo-Json) | Out-Null
    Check 'clerk product write 403' $false
} catch { Check 'clerk product write 403' ($_.Exception.Response.StatusCode -eq 403) }

# 5. Admin creates a product + currency mismatch refused (400)
$new = Invoke-RestMethod -Method Post -Uri "$base/api/products" -Headers $H -ContentType 'application/json' `
    -Body (@{ name = 'Webcam Pro'; priceAmount = 2500000; currency = 'IRR'; stockQuantity = 10 } | ConvertTo-Json)
Check 'admin creates product 201' ($new.currency -eq 'IRR') "($($new.name) id=$($new.Id.Substring(0,8)))"
try {
    Invoke-RestMethod -Method Patch -Uri "$base/api/products/$($new.Id)/price" -Headers $H -ContentType 'application/json' `
        -Body (@{ amount = 5; currency = 'USD' } | ConvertTo-Json) | Out-Null
    Check 're-price in other currency 400' $false
} catch { Check 're-price in other currency 400' ($_.Exception.Response.StatusCode -eq 400) }

# 6. Customer
$customer = Invoke-RestMethod -Method Post -Uri "$base/api/customers" -Headers $H -ContentType 'application/json' `
    -Body (@{ name = 'Smoke Trading'; address = 'Tehran, No. 1'; email = 'a@b.co' } | ConvertTo-Json)
Check 'customer created' ($customer.name -eq 'Smoke Trading')

# 7. Order lifecycle draft -> delivered
$order = Invoke-RestMethod -Method Post -Uri "$base/api/orders" -Headers $H -ContentType 'application/json' `
    -Body (@{ customerId = $customer.Id; currency = 'IRR' } | ConvertTo-Json)
Check 'draft created, numbered at creation' (("$($order.number)" -match '^ORD-\d{6}$') -and ($order.status -eq 'Draft')) "(number=$($order.number))"

$order = Invoke-RestMethod -Method Post -Uri "$base/api/orders/$($order.Id)/lines" -Headers $H -ContentType 'application/json' `
    -Body (@{ productId = $kb.Id; quantity = 3 } | ConvertTo-Json)
Check 'line added, subtotal 2550000' ($order.subtotal -eq 2550000) "(total=$($order.total))"

$order = Invoke-RestMethod -Method Post -Uri "$base/api/orders/$($order.Id)/discount/automatic" -Headers $H
Check 'auto discount applied (5% volume)' ($order.discount -eq 127500) "(total=$($order.total))"

# 8. Ship before place must be 409
try {
    Invoke-RestMethod -Method Post -Uri "$base/api/orders/$($order.Id)/ship" -Headers $H | Out-Null
    Check 'ship a draft 409' $false
} catch { Check 'ship a draft 409' ($_.Exception.Response.StatusCode -eq 409) }

$order = Invoke-RestMethod -Method Post -Uri "$base/api/orders/$($order.Id)/place" -Headers $H
Check 'placed gets ORD-000001' ($order.number -eq 'ORD-000001' -and $order.status -eq 'Placed')
$order = Invoke-RestMethod -Method Post -Uri "$base/api/orders/$($order.Id)/confirm" -Headers $H
$order = Invoke-RestMethod -Method Post -Uri "$base/api/orders/$($order.Id)/ship" -Headers $H
Check 'shipped' ($order.status -eq 'Shipped')
$order = Invoke-RestMethod -Method Post -Uri "$base/api/orders/$($order.Id)/deliver" -Headers $H
Check 'delivered' ($order.status -eq 'Delivered' -and $order.deliveredAt)

# 9. Stock deducted by ship (keyboard 40 -> 37)
$kbAfter = Invoke-RestMethod -Uri "$base/api/products/$($kb.Id)" -Headers $H
Check 'stock deducted 40->37' ($kbAfter.stockQuantity -eq 37) "(got $($kbAfter.stockQuantity))"

# 10. Cancel a placed order -> 409? (Placed->Confirmed->... cancel allowed from Placed? try empty draft cancel)
$o2 = Invoke-RestMethod -Method Post -Uri "$base/api/orders" -Headers $H -ContentType 'application/json' `
    -Body (@{ customerId = $customer.Id } | ConvertTo-Json)
try {
    Invoke-RestMethod -Method Post -Uri "$base/api/orders/$($o2.Id)/place" -Headers $H | Out-Null
    Check 'place empty draft 409/400' $false
} catch {
    $code = $_.Exception.Response.StatusCode
    Check 'place empty draft refused' ($code -eq 409 -or $code -eq 400) "($code)"
}

# 11. Bad login gives identical 401 (traceId excluded: it is per-request by design)
$b1 = try { Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' -Body (@{ userName='nobody'; password='Xy!1abcdef' } | ConvertTo-Json) } catch { $_.ErrorDetails.Message }
$b2 = try { Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' -Body (@{ userName='admin'; password='Wrong#1234' } | ConvertTo-Json) } catch { $_.ErrorDetails.Message }
$b1 = ("$b1" -replace '"traceId":\s*"[^"]*",?\s*', '') -replace '"instance":\s*"[^"]*",?\s*', ''
$b2 = ("$b2" -replace '"traceId":\s*"[^"]*",?\s*', '') -replace '"instance":\s*"[^"]*",?\s*', ''
Check 'enumeration-safe 401 bodies' ($b1 -eq $b2)

# 12. Orders by customer route: delivered order + the empty draft from step 10, newest placed first
$byCustomer = Invoke-RestMethod -Uri "$base/api/orders/by-customer/$($customer.Id)" -Headers $H
Check 'by-customer lists both documents, delivered first' (($byCustomer.Count -eq 2) -and ($byCustomer[0].status -eq 'Delivered'))

Write-Host ''
Write-Host "RESULT: $pass passed, $fail failed" -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
