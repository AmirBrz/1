using Samian.Domain.Common;
using Samian.Domain.Exceptions;

namespace Samian.Domain.Entities;

// Customer is its own aggregate root (its own repository, its own identity, changed independently
// of orders). Order only stores CustomerId.
public class Customer : AggregateRoot
{
    private Customer() { Name = null!; Address = null!; }

    // The old `public Guid Id { get; set; }` here hid Entity<Guid>.Id (CS0108) and re-opened a public
    // setter on identity — the base property is inherited, not re-declared.
    public string Name { get; private set; }

    public string Address { get; private set; }

    public static Customer Create(string name, string address)
    {
        // The factory is the invariant gate: an empty-named customer should never reach the database.
        Guard.AgainstEmpty(name, () => new DomainException("Customer name is required."));
        Guard.AgainstEmpty(address, () => new DomainException("Customer address is required."));

        return new Customer()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Address = address
        };
    }

    public Customer ChangeAddress(string address)
    {
        Guard.AgainstEmpty(address, () => new DomainException("Customer address is required."));
        Address = address;
        return this;
    }
}