using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Samian.Application.Features.Products;

namespace Samian.WebApi.Controllers;

// Catalogue reads are open to any authenticated user — a clerk needs prices to build an order —
// while every catalogue WRITE is Admin-only. Role checks live on the endpoints, not inside
// ProductService: "can this caller do it" is transport policy (whoever owns tokens and claims owns
// it too), and the application service stays usable from a background job with no caller at all.
[ApiController]
[Route("api/products")]
[Authorize]
public sealed class ProductsController : ControllerBase
{
    private readonly IProductService _products;

    public ProductsController(IProductService products)
    {
        ArgumentNullException.ThrowIfNull(products);
        _products = products;
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ProductResponse>> Create(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var response = await _products.CreateAsync(request, cancellationToken);

        // 201 + Location: unlike a token, a product IS a fetchable resource, so this is where the
        // CreatedAtAction pattern earns its place. If GetAsync is ever renamed, this throws at
        // request time — which is why the route value is spelled out instead of relying on inference.
        return CreatedAtAction(nameof(GetById), new { id = response.Id }, response);
    }

    // Page/pageSize stay plain query parameters rather than a bound PagedRequest record: two ints
    // do not need a type, and the service clamps the range (a validator's job, already there).
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProductResponse>>> List(
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var products = await _products.ListAsync(page, pageSize, cancellationToken);

        return Ok(products);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProductResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        // The 404 lives in the service (ResourceNotFoundException -> middleware), so the controller
        // never writes `is null ? NotFound() : Ok()`: one funnel means every "not found" in the API
        // has the same Problem Details shape, which clients can actually code against.
        var product = await _products.GetAsync(id, cancellationToken);

        return Ok(product);
    }

    [HttpPatch("{id:guid}/price")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ProductResponse>> ChangePrice(Guid id, ChangePriceRequest request, CancellationToken cancellationToken)
    {
        var product = await _products.ChangePriceAsync(id, request, cancellationToken);

        return Ok(product);
    }

    [HttpPatch("{id:guid}/restock")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ProductResponse>> Restock(Guid id, RestockRequest request, CancellationToken cancellationToken)
    {
        var product = await _products.RestockAsync(id, request, cancellationToken);

        return Ok(product);
    }
}
