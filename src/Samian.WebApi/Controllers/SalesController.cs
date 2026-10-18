using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Samian.Application.Features.Sales;

namespace Samian.WebApi.Controllers;

// The Black Friday endpoints (ROADMAP step 18). Thin by the AGENTS.md rule: each action binds one
// request record, calls exactly one Application interface, and returns whatever the service produced.
// The 404/409/400 shapes live in the service + exception funnel, never in here — a controller with a
// try/catch is a controller that has leaked use-case policy into transport.
//
// Tenancy is NOT a route parameter: X-Tenant-Id is validated once by TenantMiddleware before this
// controller ever runs, so a missing/unknown tenant is a 400 that never reaches an action.
[ApiController]
[Route("api/sales")]
[Authorize]
public sealed class SalesController : ControllerBase
{
    private readonly ISaleService _sales;

    public SalesController(ISaleService sales)
    {
        ArgumentNullException.ThrowIfNull(sales);
        _sales = sales;
    }

    // Creating a campaign is a catalogue WRITE → Admin-only, same rule as products (Roles live on the
    // endpoint, not in SaleService, so the service stays callable from a background job with no caller).
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<SaleStatusResponse>> Create(CreateSaleRequest request, CancellationToken cancellationToken)
    {
        var sale = await _sales.CreateAsync(request, cancellationToken);

        return CreatedAtAction(nameof(GetStatus), new { id = sale.Id }, sale);
    }

    // The burst read: remaining units + computed state. Cached 5 s, DISPLAY-ONLY (see SaleService);
    // a reservation never reads this key, so a stale badge cannot cause an oversell.
    [HttpGet("{id:guid}/status")]
    public async Task<ActionResult<SaleStatusResponse>> GetStatus(Guid id, CancellationToken cancellationToken)
    {
        var status = await _sales.GetStatusAsync(id, cancellationToken);

        return Ok(status);
    }

    // The shopper's landing view: everything ON for this tenant right now.
    [HttpGet("active")]
    public async Task<ActionResult<IReadOnlyList<SaleStatusResponse>>> ListActive(CancellationToken cancellationToken)
    {
        var sales = await _sales.ListActiveAsync(cancellationToken);

        return Ok(sales);
    }
}
