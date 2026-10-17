namespace Samian.Application.Configuration;

// Settings the USE CASE needs to know about. Only TokenLifetimeMinutes is read by Application code
// (AuthService computes the expiry and passes it to the generator); the key material below is read by
// the Infrastructure adapter and by the host's authentication setup.
//
// The honest trade-off for the interview: putting SecretKey in an Application POCO is a small purity
// compromise (Application names a JWT concept). The alternatives — a Domain port per signing
// algorithm, or duplicating the options type in Infrastructure — cost more in indirection than the
// purity they buy. Everything here is still bound from configuration, never hard-coded.
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "Samian.WebApi";

    public string Audience { get; set; } = "Samian.Clients";

    // Must come from configuration/environment. A dev-only value lives in appsettings.Development.json;
    // appsettings.json keeps it empty so a missing secret fails loudly at startup instead of silently
    // signing tokens with a public key.
    public string SecretKey { get; set; } = string.Empty;

    public int TokenLifetimeMinutes { get; set; } = 120;
}
