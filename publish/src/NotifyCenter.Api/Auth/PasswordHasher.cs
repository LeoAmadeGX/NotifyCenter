using System.Security.Cryptography;

namespace NotifyCenter.Api.Auth;

public sealed class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int DefaultIterations = 120_000;

    public PasswordHashResult HashPassword(string password)
    {
        Span<byte> salt = stackalloc byte[SaltSize];
        RandomNumberGenerator.Fill(salt);
        return HashPassword(password, salt.ToArray(), DefaultIterations);
    }

    public PasswordHashResult HashPassword(string password, byte[] salt, int iterations)
    {
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeySize);

        return new PasswordHashResult(
            Convert.ToBase64String(hash),
            Convert.ToBase64String(salt),
            iterations);
    }

    public bool Verify(
        string password,
        string expectedHash,
        string expectedSalt,
        int iterations)
    {
        if (string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(expectedHash) ||
            string.IsNullOrWhiteSpace(expectedSalt) ||
            iterations <= 0)
        {
            return false;
        }

        byte[] saltBytes;
        byte[] expectedHashBytes;
        try
        {
            saltBytes = Convert.FromBase64String(expectedSalt);
            expectedHashBytes = Convert.FromBase64String(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHashBytes = Rfc2898DeriveBytes.Pbkdf2(
            password,
            saltBytes,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHashBytes.Length);

        return CryptographicOperations.FixedTimeEquals(actualHashBytes, expectedHashBytes);
    }
}

public sealed record PasswordHashResult(string Hash, string Salt, int Iterations);
