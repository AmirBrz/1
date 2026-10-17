using System.Security.Cryptography;
using System.Text;
using Samian.Application.Contracts;

namespace Samian.Infrastructure.Security;

// PBKDF2-SHA512 from the BCL: no package, works offline, and it is a legitimate answer when asked why
// not bcrypt/Argon2 — those need a NuGet package that is not in the cache, and PBKDF2 with a high
// iteration count is what FIPS-compliant shops ship. If Argon2id were available, THIS class would be
// replaced and nothing else in the solution would change, because the domain only knows IPasswordHasher.
//
// Stored format: PBKDF2.SHA512.<iterations>.<salt-base64>.<hash-base64>
// Everything needed to verify is inside the string, so the Users table needs one column and a future
// iteration-count increase cannot invalidate old passwords (each row keeps the count it was made with).
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Prefix = "PBKDF2.SHA512";
    private const int SaltSize = 16;    // 128 bits
    private const int KeySize = 32;     // 256 bits
    private const int Iterations = 100_000;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Derive(password, salt, Iterations);

        // No culture-sensitive formatting anywhere in this string: Verify parses it back with
        // int.TryParse/Convert.FromBase64String, and a locale that groups thousands would make hashes
        // created on one machine unverifiable on another.
        return $"{Prefix}.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash))
            return false;

        var parts = hash.Split('.');

        // Five parts and our own prefix, or the stored value was written by something else (a tampered
        // row, another app sharing the table): refuse rather than guess, and never throw.
        if (parts.Length != 5 || parts[0] != "PBKDF2" || parts[1] != "SHA512")
            return false;

        if (!int.TryParse(parts[2], out var iterations) || iterations <= 0)
            return false;

        byte[] salt;
        byte[] expectedKey;

        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expectedKey = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualKey = Derive(password, salt, iterations, expectedKey.Length);

        // FixedTimeEquals: a plain == would leak, one byte at a time, how much of a guessed hash matches.
        return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int keySize = KeySize)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA512,
            keySize);
}
