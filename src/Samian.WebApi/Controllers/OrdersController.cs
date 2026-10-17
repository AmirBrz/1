using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Samian.Application.Features.Orders;

namespace Samian.WebApi.Controllers;

// The order endpoints map the AGGREGATE, not a table: a draft is a sub-resource tree (lines,
// discount) that only exists while the status allows editing, and the lifecycle transitions are
// POST verb-actions because "place this order" is a command, not an update of a column.
//
// Every transition route is a POST that returns the whole OrderResponse: the client sent an intent,
// and the answer is the new document state (with its totals and timestamps), not a bare 204.
[ApiController]
[Route("api/orders")]
[Authorize]
public sealed class OrdersController : ControllerBase
{
    private readonly IOrderService _orders;

    public OrdersController(IOrderService orders)
    {
        ArgumentNullException.ThrowIfNull(orders);
        _orders = orders;
    }

    [HttpPost]
    public async Task<ActionResult<OrderResponse>> CreateDraft(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await _orders.CreateDraftAsync(request, cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = order.Id }, order);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var order = await _orders.GetAsync(id, cancellationToken);

        return Ok(order);
    }

    [HttpGet("by-customer/{customerId:guid}")]
    public async Task<ActionResult<IReadOnlyList<OrderResponse>>> GetForCustomer(Guid customerId, CancellationToken cancellationToken)
    {
        var orders = await _orders.GetForCustomerAsync(customerId, cancellationToken);

        return Ok(orders);
    }

    // Lines live under their order because OrderLine has no identity outside the aggregate — there
    // is no GET /api/order-lines/{id} anywhere in this API, and that is the route layout saying so.
    [HttpPost("{orderId:guid}/lines")]
    public async Task<ActionResult<OrderResponse>> AddLine(Guid orderId, AddOrderLineRequest request, CancellationToken cancellationToken)
    {
        var order = await _orders.AddLineAsync(orderId, request, cancellationToken);

        return Ok(order);
    }

    [HttpPatch("{orderId:guid}/lines/{lineId:guid}/quantity")]
    public async Task<ActionResult<OrderResponse>> ChangeLineQuantity(
        Guid orderId, Guid lineId, ChangeOrderLineQuantityRequest request, CancellationToken cancellationToken)
    {
        var order = await _orders.ChangeLineQuantityAsync(orderId, lineId, request, cancellationToken);

        return Ok(order);
    }

    // 200 + full document rather than 204 NoContent: after removing a line the subtotal and total
    // changed, and a client that got a 204 would have to re-GET to learn the new numbers.
    [HttpDelete("{orderId:guid}/lines/{lineId:guid}")]
    public async Task<ActionResult<OrderResponse>> RemoveLine(Guid orderId, Guid lineId, CancellationToken cancellationToken)
    {
        var order = await _orders.RemoveLineAsync(orderId, lineId, cancellationToken);

        return Ok(order);
    }

    [HttpPost("{orderId:guid}/discount")]
    public async Task<ActionResult<OrderResponse>> ApplyDiscount(Guid orderId, ApplyDiscountRequest request, CancellationToken cancellationToken)
    {
        var order = await _orders.ApplyDiscountAsync(orderId, request, cancellationToken);

        return Ok(order);
    }

    // The pricing POLICY (strategy pattern, configured in appsettings) runs behind this route; the
    // clerk does not type a discount amount, the volume rule computes it.
    [HttpPost("{orderId:guid}/discount/automatic")]
    public async Task<ActionResult<OrderResponse>> ApplyAutomaticDiscount(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _orders.ApplyAutomaticDiscountAsync(orderId, cancellationToken);

        return Ok(order);
    }

    // State machine routes: one POST per transition, in the same order as OrderStatus. Trying to
    // ship a Draft answers 409 with the aggregate's own sentence ("...can only change while it is a
    // Draft" and friends) — the controller adds nothing to that, it just carries it.
    [HttpPost("{orderId:guid}/place")]
    public async Task<ActionResult<OrderResponse>> Place(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _orders.PlaceAsync(orderId, cancellationToken);

        return Ok(order);
    }

    [HttpPost("{orderId:guid}/confirm")]
    public async Task<ActionResult<OrderResponse>> Confirm(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _orders.ConfirmAsync(orderId, cancellationToken);

        return Ok(order);
    }

    [HttpPost("{orderId:guid}/ship")]
    public async Task<ActionResult<OrderResponse>> Ship(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _orders.ShipAsync(orderId, cancellationToken);

        return Ok(order);
    }

    [HttpPost("{orderId:guid}/deliver")]
    public async Task<ActionResult<OrderResponse>> Deliver(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _orders.DeliverAsync(orderId, cancellationToken);

        return Ok(order);
    }

    [HttpPost("{orderId:guid}/cancel")]
    public async Task<ActionResult<OrderResponse>> Cancel(Guid orderId, CancelOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await _orders.CancelAsync(orderId, request, cancellationToken);

        return Ok(order);
    }
}
