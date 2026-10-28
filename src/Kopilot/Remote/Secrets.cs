using System.Security.Cryptography;
using System.Text;

namespace Kopilot.Remote;

/// <summary>Zufallswerte, Hashes und zeitkonstante Vergleiche.</summary>
public static class Secrets
{
    /// <summary>Kryptografisch zufälliger, URL-sicherer String (32 Byte → 43 Zeichen).</summary>
    public static string NewToken(string prefix = "") => prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Base64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>SHA-256 als Hex – so werden Tokens gespeichert, nie im Klartext.</summary>
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Zeitkonstanter Vergleich (über die Hashes, damit auch die Länge nicht durchsickert).</summary>
    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

    /// <summary>PKCE S256: BASE64URL(SHA256(code_verifier)).</summary>
    public static string PkceChallenge(string codeVerifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
}
