using System.Security.Cryptography;

namespace Boxy.Web.Services;

/// <summary>Short, URL-safe, unguessable tokens for public bucket and share URLs.</summary>
public static class SlugGenerator
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789"; // no look-alikes (l/1/0/o)

    public static string New(int length = 10)
    {
        var chars = new char[length];
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(chars);
    }
}
