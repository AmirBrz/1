using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Samian.Application.Features.Customers;

namespace Samian.WebApi.Controllers;

// Customers are the smallest slice in the API — three endpoints, no list, no delete — and that
// minimalism is itself a statement: a record the Order aggregate points at does not need a
// management console to exist. When the real product grows one, this controller grows; the layers
// underneath already answer the same questions through ICustomerService.
[ApiController]
[Route("api/customers")]
[Authorize]
public sealed class CustomersController : ControllerBase
{
    private readonly ICustomerService _customers;

    public CustomersController(ICustomerService customerService) => _customers = customerService;

    [HttpPost]
    public async Task<ActionResult<CustomerResponse>> Create(CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        var customer = await _customers.CreateAsync(request, cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = customer.Id }, customer);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CustomerResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var customer = await _customers.GetAsync(id, cancellationToken);

        return Ok(customer);
    }

    // PATCH, not PUT: the request carries exactly the field that changes. A PUT /email that replaced
    // the whole customer would let a stale form overwrite a concurrent address edit — the verb here
    // describes the actual blast radius of the operation.
    [HttpPatch("{id:guid}/email")]
    public async Task<ActionResult<CustomerResponse>> ChangeEmail(
        Guid id, ChangeCustomerEmailRequest request, CancellationToken cancellationToken)
    {
        var customer = await _customers.ChangeEmailAsync(id, request, cancellationToken);

        return Ok(customer);
    }
}
