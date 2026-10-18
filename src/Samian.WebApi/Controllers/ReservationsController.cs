using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Samian.Application.Features.Reservations;

namespace Samian.WebApi.Controllers;

// THE endpoint the whole challenge is about: a shopper reserves units and gets an INSTANT answer,
// while fulfilment (order/warehouse/notification) is handed to the outbox pipeline out of band.
//
// One Application interface, zero try/catch: sold-out and closed-window are BusinessConflictExceptions
// the funnel turns into 409, and the controller never inspects them (AGENTS.md error rule).
[ApiController]
[Route("api/reservations")]
[Authorize]
public sealed class ReservationsController : ControllerBase
{
    private readonly IReservationService _reservations;

    public ReservationsController(IReservationService reservations)
    {
        ArgumentNullException.ThrowIfNull(reservations);
        _reservations = reservations;
    }

    // 201 Created: a reservation is a real, fetchable record of who took what — the durable proof
    // the atomic decrement left behind. Under a 500-request burst this is the only route that moves
    // RemainingUnits, and it moves it with one conditional UPDATE (SaleEventRepository), never a
    // read-then-write, so two simultaneous callers cannot both pass the same last-unit check.
    [HttpPost]
    public async Task<ActionResult<ReservationResponse>> Reserve(ReserveRequest request, CancellationToken cancellationToken)
    {
        var reservation = await _reservations.ReserveAsync(request, cancellationToken);

        // No GET-by-id action exists (a reservation is read through the pipeline view), so the
        // Location points at the sale's status — the resource whose counters this call just changed.
        return CreatedAtAction(
            nameof(SalesController.GetStatus),
            "sales",
            new { id = reservation.SaleEventId },
            reservation);
    }
}
