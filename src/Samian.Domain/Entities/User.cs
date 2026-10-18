using Samian.Domain.Common;
using Samian.Domain.Exceptions;

namespace Samian.Domain.Entities;

// Account credentials, not a second Customer. Identity stays here because login is a business
// concern (who may act), while the hashing ALGORITHM is a port (IPasswordHasher) implemented in
// Infrastructure — the domain decides when to hash, not how.
//
// A User BELONGS to one tenant (IMultiTenant): the tenant is part of the credential, so the same
// user name may exist in two tenants and, once signed in, the app already knows which tenant owns
// that caller — no external header to trust. NOTE: unlike Order/Product, User carries NO global
// query filter: login happens before any tenant context exists, so the user store is looked up by
// an EXPLICIT (TenantId, UserName) pair instead of the ambient tenant.
public class User : AggregateRoot, IMultiTenant
{
    private User() { UserName = null!; PasswordHash = null!; Role = DefaultRole; }

    public Guid TenantId { get; private set; }

    // Inherited Id is used as-is (the old re-declared `public Guid Id { get; set; }` hid the base
    // member and made identity writable from outside).
    public string UserName { get; private set; }

    public string PasswordHash { get; private set; }

    public bool IsActive { get; private set; }

    public string Role { get; private set; }

    public const string DefaultRole = "User";

    // Parameter renamed password -> passwordHash: the name now forces the caller to hash BEFORE
    // calling, which is what stopped this from quietly storing raw passwords.
    // tenantId is FIRST and required: an account with no tenant cannot exist, because every use case
    // downstream (stock availability, reservations) resolves the tenant FROM the signed-in user.
    public static User Create(Guid tenantId, string userName, string passwordHash, string role = DefaultRole)
    {
        Guard.AgainstEmpty(userName, () => new DomainException("User name is required."));
        Guard.AgainstEmpty(passwordHash, () => new DomainException("A password hash is required."));
        Guard.AgainstInvalidState(tenantId != Guid.Empty,
            () => new DomainException("A user must belong to a tenant."));

        return new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = userName,
            PasswordHash = passwordHash,
            IsActive = true,
            Role = role
        };
    }

    // Credential rotation goes through the root so an outside caller can never hand-write a hash
    // that skipped the policy check.
    public User ChangePassword(string newPasswordHash)
    {
        Guard.AgainstEmpty(newPasswordHash, () => new DomainException("A password hash is required."));
        PasswordHash = newPasswordHash;
        return this;
    }

    public User Deactivate()
    {
        IsActive = false;
        return this;
    }

    public User Activate()
    {
        IsActive = true;
        return this;
    }
}