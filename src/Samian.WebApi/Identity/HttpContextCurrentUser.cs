using System.Security.Claims;
using Samian.Application.Contracts;

namespace Samian.WebApi.Identity;

// The HTTP ADAPTER for ICurrentUser. Application asked "who is calling"; only the host can answer that,
// because the answer lives in the Authorization header. Nothing above this class knows a ClaimsPrincipal
// exists — which is what lets the same application service run from a background worker later.
//
// Registered SCOPED: it reads the current request's principal, so a singleton would freeze the first
// caller's identity for the life of the process. That is a security bug with a very confusing symptom.
public sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly ClaimsPrincipal? _principal;

    public HttpContextCurrentUser(IHttpContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);

        // Nullable on purpose: the accessor returns an unauthenticated (or empty) principal for
        // anonymous endpoints, and "no user" must not be an exception in a constructor.
        _principal = accessor.HttpContext?.User;
    }

    public bool IsAuthenticated => _principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId
    {
        get
        {
            var raw = _principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? _principal?.FindFirstValue("sub");

            // TryParse instead of Guid.Parse: a hand-crafted token with a non-Guid subject should make
            // this user "anonymous-ish" to the service, not throw inside a property getter.
            return Guid.TryParse(raw, out var userId) ? userId : null;
        }
    }

    public string? UserName =>
        _principal?.FindFirstValue(ClaimTypes.Name) ?? _principal?.FindFirstValue("unique_name");

    public string? Role =>
        _principal?.FindFirstValue(ClaimTypes.Role) ?? _principal?.FindFirstValue("role");

    // IsInRole (not Role == role): the JwtBearer handler stores multiple roles as multiple claims, and a
    // string compare would silently miss the second one.
    public bool IsInRole(string role) => _principal?.IsInRole(role) == true;
}
