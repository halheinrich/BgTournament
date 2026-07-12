using System.Security.Cryptography;
using System.Text;

namespace BgTournament.Server;

/// <summary>
/// A roster credential at rest, server-side: the salted hash of an issued
/// engine key plus the scheme that produced it. The plaintext key exists only
/// in the moment of issuance; state and journal hold this instead.
/// </summary>
/// <param name="Scheme">The hash scheme id (<see cref="EngineKeyCredentials.Scheme"/> for everything this server issues).</param>
/// <param name="Salt">The per-credential random salt, lowercase hex.</param>
/// <param name="Hash">The salted hash of the key, lowercase hex.</param>
internal sealed record EngineCredential(string Scheme, string Salt, string Hash);

/// <summary>
/// The engine-key credential scheme: generation, derivation, verification.
///
/// <para><b>Salted SHA-256, deliberately not a stretched KDF.</b> Engine keys
/// are server-generated 256-bit CSPRNG values, never human-chosen passwords —
/// there is no low-entropy secret for PBKDF2/Argon2 stretching to protect,
/// and a stretched hash would put a deliberate CPU cost on every hello
/// (a handshake DoS surface). The salt and the per-credential
/// <see cref="Scheme"/> id are future-proofing: a scheme migration is
/// per-entry data, not a format break.</para>
/// </summary>
internal static class EngineKeyCredentials
{
    /// <summary>The scheme id this server records on every credential it issues.</summary>
    public const string Scheme = "sha256-salted-v1";

    /// <summary>Generate a fresh engine key: 32 CSPRNG bytes as lowercase hex (the DiceKey format precedent).</summary>
    public static string GenerateKey() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>Derive the at-rest credential for a freshly issued key: new random salt, salted SHA-256.</summary>
    public static EngineCredential Derive(string engineKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(engineKey);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return new EngineCredential(
            Scheme, Convert.ToHexStringLower(salt), Convert.ToHexStringLower(Hash(salt, engineKey)));
    }

    /// <summary>
    /// Whether a presented key matches a stored credential. Fixed-time on the
    /// hash comparison; false (never a throw) for an unknown scheme or
    /// undecodable credential material — a credential this server cannot
    /// check authenticates nothing, and the admin remedy is rotation.
    /// </summary>
    public static bool Verifies(string presentedKey, EngineCredential credential)
    {
        if (credential.Scheme != Scheme)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Hash(Convert.FromHexString(credential.Salt), presentedKey),
                Convert.FromHexString(credential.Hash));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Hash(byte[] salt, string engineKey)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(engineKey);
        byte[] input = new byte[salt.Length + keyBytes.Length];
        salt.CopyTo(input, 0);
        keyBytes.CopyTo(input, salt.Length);
        return SHA256.HashData(input);
    }
}
