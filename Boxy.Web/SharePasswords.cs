using Microsoft.AspNetCore.Identity;

namespace Boxy.Web;

/// <summary>Hash and verify a share's optional access password with the framework's PBKDF2 hasher -
/// the same primitive used for account passwords, so shares get the same protection.</summary>
public static class SharePasswords
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Subject = new();

    public static string Hash(string password)
    {
        return Hasher.HashPassword(Subject, password);
    }

    public static bool Verify(string hash, string password)
    {
        return Hasher.VerifyHashedPassword(Subject, hash, password) != PasswordVerificationResult.Failed;
    }
}
