namespace Samian.Application.Contracts;

// WHO is calling, as far as the application layer cares. The HTTP/JWT mechanics of reading claims
// live in the host (WebApi implements this interface); Application and Domain only ever ask
// "which user id / which role", which is why no ASP.NET type appears here.
public interface ICurrentUser
{
    Guid? UserId { get; }

    string? UserName { get; }

    string? Role { get; }

    bool IsAuthenticated { get; }

    bool IsInRole(string role);
}

// The token FORMAT is an outside concern (it is a port, so Application can be unit-tested without
// any JWT library), but issuing it is a use-case step, so the contract is declared here.
public interface IJwtTokenGenerator
{
    // expiresAtUtc is a parameter instead of `DateTime.UtcNow.AddHours(2)` inside the implementation:
    // the token lifetime is a policy decision made by the use case, and baking the clock into the
    // generator would make "does it really expire?" untestable.
    // tenantId is stamped INTO the token so the signed-in caller's tenant travels with every request:
    // the app reads the tenant back from the credential instead of trusting a client-supplied header.
    string GenerateToken(Guid userId, string userName, string role, Guid tenantId, DateTimeOffset expiresAtUtc);
}

// The domain decided WHEN a password must be hashed (User.Create takes a hash, never a plain
// password); this port says the application needs the hashing done, and Infrastructure says how
// (PBKDF2 here, Argon2id if the security policy changes — no use-case code changes).
public interface IPasswordHasher
{
    string Hash(string password);

    // Implementations must be constant-time comparisons and must carry the salt/parameters inside
    // the stored string, so Verify needs no second column.
    bool Verify(string password, string hash);
}
