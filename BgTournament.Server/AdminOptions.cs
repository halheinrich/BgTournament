namespace BgTournament.Server;

/// <summary>
/// Server-configured admin identity, bound from the <c>Admin</c> configuration
/// section: the named API keys that gate the admin HTTP surface. Key
/// <em>names</em> are the actor identities the durable record stamps on admin
/// actions; key <em>values</em> are the secrets requests present in the
/// <c>X-Api-Key</c> header. Plaintext by design: the configuration shares the
/// server process's trust domain, exactly like the fair-mode dice keys under
/// <c>Persistence:DataDirectory</c>.
///
/// <para>Enforcement is implicit in the configuration: any key configured
/// means every admin request must present a valid one; an empty section means
/// the surface serves anonymously (today's behavior — the dev/smoke default),
/// announced loudly at startup. There is no separate enforcement flag, so no
/// invalid "enforce with no keys" state exists to fail on.</para>
/// </summary>
internal sealed class AdminOptions
{
    /// <summary>
    /// The named admin API keys: actor name → key value. Empty (the default)
    /// serves the admin surface anonymously.
    /// </summary>
    public Dictionary<string, string> ApiKeys { get; set; } = [];
}
