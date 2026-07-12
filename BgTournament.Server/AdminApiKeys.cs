using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BgTournament.Server;

/// <summary>
/// The validated admin key set — <see cref="AdminOptions"/> checked once at
/// startup and frozen. Construction fails loudly on a broken configuration
/// (blank name or key, or two names sharing one key value, which would make
/// the actor identity ambiguous): a server that cannot know who its keys name
/// must not start. The mode is announced at startup either way, because an
/// anonymously-serving surface should never be a silent surprise.
/// </summary>
internal sealed class AdminApiKeys
{
    private readonly (string Name, byte[] Key)[] _keys;

    public AdminApiKeys(IOptions<AdminOptions> options, ILogger<AdminApiKeys> logger)
    {
        var configured = options.Value.ApiKeys;
        var keys = new List<(string Name, byte[] Key)>(configured.Count);
        foreach ((string name, string key) in configured)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    "Admin:ApiKeys contains a blank actor name; every key must be named — "
                        + "the name is the actor identity the durable record stamps.");
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException(
                    $"Admin:ApiKeys['{name}'] has a blank key value; configure a secret or remove the entry.");
            }

            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            foreach ((string otherName, byte[] otherKey) in keys)
            {
                if (CryptographicOperations.FixedTimeEquals(keyBytes, otherKey))
                {
                    throw new InvalidOperationException(
                        $"Admin:ApiKeys['{name}'] and Admin:ApiKeys['{otherName}'] share one key value; "
                            + "key values must be distinct — a shared key has no single actor identity.");
                }
            }

            keys.Add((name, keyBytes));
        }

        _keys = keys.ToArray();
        if (Enforcing)
        {
            logger.LogInformation(
                "Admin surface enforcing API keys for {ActorCount} actor(s): {Actors}.",
                _keys.Length, string.Join(", ", _keys.Select(entry => entry.Name)));
        }
        else
        {
            logger.LogWarning(
                "Admin surface is serving anonymously — no Admin:ApiKeys are configured.");
        }
    }

    /// <summary>
    /// Whether admin requests must present a valid key — true exactly when
    /// any key is configured.
    /// </summary>
    public bool Enforcing => _keys.Length > 0;

    /// <summary>
    /// Resolve a presented key value to its actor name. Fixed-time comparison
    /// against every configured key, so the check leaks nothing about how far
    /// a guess matched.
    /// </summary>
    public bool TryIdentify(string presentedKey, out string actor)
    {
        byte[] presentedBytes = Encoding.UTF8.GetBytes(presentedKey);
        foreach ((string name, byte[] key) in _keys)
        {
            if (CryptographicOperations.FixedTimeEquals(presentedBytes, key))
            {
                actor = name;
                return true;
            }
        }

        actor = string.Empty;
        return false;
    }
}
