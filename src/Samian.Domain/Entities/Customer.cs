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

    // Nullable: a customer can be registered before anybody knows how to email them, and a required
    // email would force fake data into the sample. Order handlers use it only as an ADDRESS, never as
    // a business rule — no invariant depends on it.
    public string? Email { get; private set; }

    public static Customer Create(string name, string address, string? email = null)
    {
        // The factory is the invariant gate: an empty-named customer should never reach the database.
        Guard.AgainstEmpty(name, () => new DomainException("Customer name is required."));
        Guard.AgainstEmpty(address, () => new DomainException("Customer address is required."));

        return new Customer()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Address = address,
            Email = email
        };
    }

    public Customer ChangeAddress(string address)
    {
        Guard.AgainstEmpty(address, () => new DomainException("Customer address is required."));
        Address = address;
        return this;
    }

    // Contact details rotate through the root for the same reason credentials do: an unvalidated write
    // would let "support@" reach a subscriber that then silently fails to send.
    public Customer ChangeEmail(string? email)
    {
        Guard.AgainstEmpty(email ?? string.Empty, () => new DomainException("Customer e-mail is required."));
        Email = email;
        return this;
    }
}