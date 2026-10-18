using Samian.Application.Common;
using Samian.Application.Contracts;
using Samian.Application.Validation;
using Samian.Domain.Contracts;
using Samian.Domain.Entities;

namespace Samian.Application.Features.Customers;

public sealed record CreateCustomerRequest(string Name, string Address, string? Email = null);

public sealed record ChangeCustomerEmailRequest(string? Email);

public sealed record CustomerResponse(Guid Id, string Name, string Address, string? Email);

public interface ICustomerService
{
    Task<CustomerResponse> CreateAsync(CreateCustomerRequest request, CancellationToken cancellationToken = default);

    Task<CustomerResponse> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CustomerResponse> ChangeEmailAsync(Guid id, ChangeCustomerEmailRequest request, CancellationToken cancellationToken = default);
}

public sealed class CreateCustomerRequestValidator : IValidator<CreateCustomerRequest>
{
    public IReadOnlyCollection<string> Validate(CreateCustomerRequest target)
    {
        var messages = new ValidationMessages();

        messages.AddIf(string.IsNullOrWhiteSpace(target.Name) || target.Name.Length > 100,
            "Customer name is required and must be 100 characters or fewer.");
        messages.AddIf(string.IsNullOrWhiteSpace(target.Address) || target.Address.Length > 300,
            "Customer address is required and must be 300 characters or fewer.");
        messages.AddIf(target.Email is not null && !RegexRules.Email().IsMatch(target.Email),
            "E-mail must look like name@domain.tld.");

        return messages.ToList();
    }
}

public sealed class CustomerService : ICustomerService
{
    private readonly ICustomerRepository _customers;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IValidator<CreateCustomerRequest> _createValidator;
    private readonly ITenantProvider _tenant;

    public CustomerService(
        ICustomerRepository customers,
        IUnitOfWork unitOfWork,
        IValidator<CreateCustomerRequest> createValidator,
        ITenantProvider tenant)
    {
        ArgumentNullException.ThrowIfNull(customers);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(tenant);

        _customers = customers;
        _unitOfWork = unitOfWork;
        _createValidator = createValidator;
        _tenant = tenant;
    }

    public async Task<CustomerResponse> CreateAsync(CreateCustomerRequest request, CancellationToken cancellationToken = default)
    {
        _createValidator.EnsureValid(request);

        // A customer belongs to the signed-in tenant; without the id here it would be stored with
        // TenantId = Guid.Empty and the global filter would make it invisible to every real-tenant read.
        if (!_tenant.HasTenant || _tenant.TenantId == Guid.Empty)
            throw new AppValidationException("A tenant context is required to create a customer (sign in to a tenant, or send X-Tenant-Id).");

        var customer = Customer.Create(request.Name, request.Address, request.Email, _tenant.TenantId);

        await _customers.AddAsync(customer, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToResponse(customer);
    }

    public async Task<CustomerResponse> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Deliberately NOT cached: this read exists so an order handler or a support screen can see the
        // address of record, and a five-minute-stale customer is worse than one extra query. Caching is
        // a per-read decision, not a blanket policy — that is why ICacheService is a dependency of the
        // services that need it instead of being wrapped around every repository.
        var customer = await _customers.GetByIdAsync(id, cancellationToken)
                       ?? throw new ResourceNotFoundException("Customer", id);

        return ToResponse(customer);
    }

    public async Task<CustomerResponse> ChangeEmailAsync(Guid id, ChangeCustomerEmailRequest request, CancellationToken cancellationToken = default)
    {
        var customer = await _customers.GetByIdAsync(id, cancellationToken)
                       ?? throw new ResourceNotFoundException("Customer", id);

        if (request.Email is not null && !RegexRules.Email().IsMatch(request.Email))
            throw new AppValidationException("E-mail must look like name@domain.tld.");

        customer.ChangeEmail(request.Email);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToResponse(customer);
    }

    private static CustomerResponse ToResponse(Customer customer) =>
        new(customer.Id, customer.Name, customer.Address, customer.Email);
}
