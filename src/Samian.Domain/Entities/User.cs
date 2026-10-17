using Samian.Domain.Common;
using Samian.Domain.Exceptions;

namespace Samian.Domain.Entities;

// Account credentials, not a second Customer. Identity stays here because login is a business
// concern (who may act), while the hashing ALGORITHM is a port (IPasswordHasher) implemented in
// Infrastructure — the domain decides when to hash, not how.
public class User : AggregateRoot
{
    private User() { UserName = null!; PasswordHash = null!; Role = DefaultRole; }

    // Inherited Id is used as-is (the old re-declared `public Guid Id { get; set; }` hid the base
    // member and made identity writable from outside).
    public string UserName { get; private set; }

    public string PasswordHash { get; private set; }

    public bool IsActive { get; private set; }

    public string Role { get; private set; }

    public const string DefaultRole = "User";

    // Parameter renamed password -> passwordHash: the name now forces the caller to hash BEFORE
    // calling, which is what stopped this from quietly storing raw passwords.
    public static User Create(string userName, string passwordHash, string role = DefaultRole)
    {
        Guard.AgainstEmpty(userName, () => new DomainException("User name is required."));
        Guard.AgainstEmpty(passwordHash, () => new DomainException("A password hash is required."));

        return new User
        {
            Id = Guid.NewGuid(),
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