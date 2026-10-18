using Microsoft.Extensions.Options;
using Samian.Application.Common;
using Samian.Application.Configuration;
using Samian.Application.Contracts;
using Samian.Application.Validation;
using Samian.Domain.Contracts;
using Samian.Domain.Entities;

namespace Samian.Application.Features.Auth;

// One folder, one file, one feature: the request DTOs, the validators, the interface and the
// implementation of "authenticate a user" sit together, so reading the file tells you the whole use
// case. This is the vertical-slice alternative to a Dtos/ + Interfaces/ + Services/ + Validators/
// matrix, and it is why no controller needs to know anything about users.
public sealed record RegisterRequest(string UserName, string Password, Guid TenantId)
{
    // Optional extra: shows that a request can carry fields the domain does not model yet, without
    // forcing them through User.Create().
    public string? Mobile { get; init; }
}

// The tenant is part of the CREDENTIAL, not an ambient header: the same user name can exist in two
// tenants, and "which tenant am I signing into" is a fact the caller states when authenticating. From
// this moment every downstream read (stock availability, reservations) is scoped to THIS tenant, read
// back from the signed-in user's token — never re-trusted from a request header.
public sealed record LoginRequest(string UserName, string Password, Guid TenantId);

public sealed record AuthResponse(
    string Token,
    DateTimeOffset ExpiresAtUtc,
    Guid UserId,
    string UserName,
    string Role,
    Guid TenantId);

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
}

public sealed class RegisterRequestValidator : IValidator<RegisterRequest>
{
    public IReadOnlyCollection<string> Validate(RegisterRequest target)
    {
        var messages = new ValidationMessages();

        messages.AddIfNot(RegexRules.UserName().IsMatch(target.UserName),
            "User name must be 3-20 characters, start with a letter, and contain only letters, digits, '.', '_' or '-'.");
        messages.AddIfNot(RegexRules.StrongPassword().IsMatch(target.Password),
            "Password must be 8-64 characters and include an upper-case letter, a lower-case letter, a digit and a symbol.");
        messages.AddIf(target.Password == target.UserName,
            "Password must not be the same as the user name.");
        messages.AddIf(target.Mobile is not null && !RegexRules.IranianMobile().IsMatch(target.Mobile),
            "Mobile must look like 09xxxxxxxxx or +989xxxxxxxxx.");
        messages.AddIf(target.TenantId == Guid.Empty,
            "A tenant is required to register: an account belongs to exactly one tenant.");

        return messages.ToList();
    }
}

public sealed class LoginRequestValidator : IValidator<LoginRequest>
{
    public IReadOnlyCollection<string> Validate(LoginRequest target)
    {
        var messages = new ValidationMessages();

        // Deliberately LOOSER than registration: an account created before today's rules must still be
        // able to sign in, so login checks "is this even a user name", and the hash check does the rest.
        messages.AddIfNot(RegexRules.UserName().IsMatch(target.UserName), "User name is not in a valid format.");
        messages.AddIf(string.IsNullOrEmpty(target.Password), "Password is required.");
        // The tenant is part of the credential; a missing one is not a slow-path guess, it is refused.
        messages.AddIf(target.TenantId == Guid.Empty, "A tenant is required to log in (it is part of the credential).");

        return messages.ToList();
    }
}

public sealed class AuthService : IAuthService
{
    private readonly IUserRepository _users;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _tokenGenerator;
    private readonly IClock _clock;
    private readonly IValidator<RegisterRequest> _registerValidator;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly JwtOptions _jwtOptions;

    public AuthService(
        IUserRepository users,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator tokenGenerator,
        IClock clock,
        IValidator<RegisterRequest> registerValidator,
        IValidator<LoginRequest> loginValidator,
        IOptions<JwtOptions> jwtOptions)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(tokenGenerator);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(registerValidator);
        ArgumentNullException.ThrowIfNull(loginValidator);
        ArgumentNullException.ThrowIfNull(jwtOptions);

        _users = users;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _tokenGenerator = tokenGenerator;
        _clock = clock;
        _registerValidator = registerValidator;
        _loginValidator = loginValidator;
        _jwtOptions = jwtOptions.Value;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        _registerValidator.EnsureValid(request);

        // Uniqueness is an APPLICATION rule, not a domain rule: User.Create() cannot know what is in the
        // database, and the entity should not have to. The database unique index is the real guard —
        // this check only exists to produce a friendly 409 instead of a constraint violation.
        // It is PER TENANT now: the same name in two tenants is two legitimate accounts.
        var existing = await _users.GetByUserNameAsync(request.TenantId, request.UserName, cancellationToken);
        if (existing is not null)
            throw new BusinessConflictException($"User name '{request.UserName}' is already taken in this tenant.");

        // Hash here, never inside the entity: the domain must not know PBKDF2 exists, but it must refuse
        // to hold a plain password — which is why User.Create's parameter is named passwordHash.
        var user = User.Create(request.TenantId, request.UserName, _passwordHasher.Hash(request.Password));

        await _users.AddAsync(user, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return IssueToken(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        _loginValidator.EnsureValid(request);

        // The (tenant, user name) pair is the identity: the same name under a different tenant is a
        // different account, and a login aimed at the wrong tenant finds nothing (same 401 as a wrong
        // password — no signal about whether the name exists in ANOTHER tenant).
        var user = await _users.GetByUserNameAsync(request.TenantId, request.UserName, cancellationToken);

        // Same exception (and therefore the same 401 body) whether the name is unknown or the password
        // is wrong: an endpoint that answers "no such user" is a user-name enumeration oracle.
        if (user is null || !user.IsActive || !_passwordHasher.Verify(request.Password, user.PasswordHash))
            throw new InvalidCredentialsException();

        return IssueToken(user);
    }

    private AuthResponse IssueToken(User user)
    {
        // The expiry is computed HERE (a use-case decision) and passed to the generator, so the token
        // adapter never decides policy and the use case never knows how the token is signed.
        var now = _clock.UtcNow;
        var expiresAtUtc = now.AddMinutes(_jwtOptions.TokenLifetimeMinutes);

        var token = _tokenGenerator.GenerateToken(user.Id, user.UserName, user.Role, user.TenantId, expiresAtUtc);

        return new AuthResponse(token, expiresAtUtc, user.Id, user.UserName, user.Role, user.TenantId);
    }
}
