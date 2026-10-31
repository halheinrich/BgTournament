namespace BgTournament.Server.Persistence;

/// <summary>
/// Server-configured persistence, bound from the <c>Persistence</c>
/// configuration section: where the durable record journals live.
/// </summary>
internal sealed class PersistenceOptions
{
    /// <summary>
    /// The data directory holding the journals (<c>matches/</c>,
    /// <c>tournaments/</c>, <c>server/</c> and <c>roster/</c> beneath it). A
    /// relative path resolves against the host's content root. The directory holds
    /// fair-mode dice keys (escrowed for interrupted-match verifiability), so
    /// it shares the server process's trust domain — do not expose it.
    /// </summary>
    public string DataDirectory { get; set; } = "data";
}
