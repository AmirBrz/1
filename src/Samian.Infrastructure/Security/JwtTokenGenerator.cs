using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Samian.Application.Configuration;
using Samian.Application.Contracts;

namespace Samian.Infrastructure.Security;

// IJwtTokenGenerator, implemented with the BCL/Microsoft identity stack. HS256 (one shared secret) is
// the right choice for a single-service API; RS256 (a public/private pair) is what you need the moment
// several services must validate tokens without sharing a secret — say that out loud in the interview,
// because it is the question that follows "why HMAC?".
public sealed class JwtTokenGenerator : IJwtTokenGenerator
{
    // The claim that carries the signed-in user's tenant. One const both the issuer here and the
    // host's TenantMiddleware read, so the string that names the tenant is defined exactly once.
    public const string TenantClaimType = "tenant_id";

    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _signingKey;

    public JwtTokenGenerator(IOptions<JwtOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;

        // Fail at STARTUP, not at the first login: an empty or short secret otherwise produces a
        // confusing SecurityTokenException inside an authentication path. HS256 requires at least 256
        // bits of key material, which is 32 characters — a secret shorter than that is not a secret.
        if (string.IsNullOrWhiteSpace(_options.SecretKey) || Encoding.UTF8.GetByteCount(_options.SecretKey) < 32)
            throw new InvalidOperationException(
                "Jwt:SecretKey is missing or shorter than 32 characters; a signing key must be configured per environment.");

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SecretKey));
    }

    public string GenerateToken(Guid userId, string userName, string role, Guid tenantId, DateTimeOffset expiresAtUtc)
    {
        var claims = new List<Claim>
        {
            // "sub" is the standards-tracker subject; ClaimTypes.Name/Role are what [Authorize(Roles=…)]
            // and ICurrentUser read after the inbound mapping the JwtBearer handler applies by default.
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.Role, role),
            // The tenant is part of the credential, not a header the client can flip: everything that
            // scopes a query to one tenant (the global filter, stock availability, reservations) reads
            // THIS claim, so a signed-in user can only ever act inside their own tenant.
            new(TenantClaimType, tenantId.ToString()),
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Expires = expiresAtUtc.UtcDateTime,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256),
        };

        var handler = new JwtSecurityTokenHandler();

        return handler.WriteToken(handler.CreateToken(descriptor));
    }
}
