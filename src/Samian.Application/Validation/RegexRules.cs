using System.Text.RegularExpressions;

namespace Samian.Application.Validation;

// The challenge asks for regex validation, and this is where it lives: ONE catalog of patterns, so a
// rule is stated once and every request type reuses it instead of copy-pasting a string literal.
//
// [GeneratedRegex] (built into .NET 8 — no package, works offline) compiles the pattern into C# code
// at build time: faster than RegexOptions.Compiled, and a syntax error fails the BUILD instead of a
// customer's request. `partial` is mandatory, because the source generator writes the other half.
public static partial class RegexRules
{
    // 3-20 chars, must start with a letter, then letters/digits/._- . Anchored, so no partial match
    // can smuggle a newline or trailing space through.
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9._-]{2,19}$")]
    public static partial Regex UserName();

    // 8-64 chars with at least one lower, one upper, one digit and one symbol. Four lookaheads look
    // cryptic, but they are the compact way to say "all four classes required" without enumerating
    // every ordering. Ready to defend: this is a floor, not a guarantee of entropy.
    [GeneratedRegex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z0-9]).{8,64}$")]
    public static partial Regex StrongPassword();

    // ISO-4217 style three uppercase letters — the same rule Money enforces, restated here so the
    // request fails with a field-level message instead of a thrown DomainException.
    [GeneratedRegex(@"^[A-Z]{3}$")]
    public static partial Regex CurrencyCode();

    // Must stay identical to DocumentNumber's own pattern: a request that passes here can never fail
    // there. Duplication is deliberate (documented in the value object) and is the price of validating
    // at the edge instead of trusting the caller.
    [GeneratedRegex(@"^[A-Z]{3}-\d{6}$")]
    public static partial Regex DocumentNumber();

    // Letters/digits and a small set of separators, must start with a letter, 2-100 long. No '<' or
    // '"' ever reaches a product name, which keeps the catalogue safe to render in any UI.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9 .&/()\-]{1,99}$")]
    public static partial Regex ProductName();

    // Persian/Gregorian mobile or landline, digits and an optional leading 0: a format check, NOT
    // proof that a number exists (no SMS verification here).
    [GeneratedRegex(@"^(\+98|0)9\d{9}$")]
    public static partial Regex IranianMobile();

    // "something@something.tld", no spaces, at most one '@'. This is a SYNTAX check: proving an address
    // exists means sending to it, which is out of scope. Being honest about that difference is the
    // interview answer when somebody asks why not the full RFC 5322 pattern.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")]
    public static partial Regex Email();

    // SKUs are machine identifiers, not prose: no spaces, no punctuation beyond '-'.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,39}$")]
    public static partial Regex Sku();
}
