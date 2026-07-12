namespace BgTournament.Server.Persistence;

/// <summary>Which journal family a store operation addresses.</summary>
internal enum JournalKind
{
    /// <summary>A per-match journal.</summary>
    Match,

    /// <summary>A per-tournament journal.</summary>
    Tournament,

    /// <summary>A per-server-session journal segment (engine lifecycle evidence).</summary>
    Server,

    /// <summary>
    /// A roster journal segment (engine registration history). Like
    /// <see cref="Server"/>, one segment per server session that mutates the
    /// roster — the roster's full history is the fold of every segment, which
    /// is what keeps this store's create-once, append-only contract intact
    /// for a record that outlives any one boot.
    /// </summary>
    Roster,
}

/// <summary>
/// The persistence seam between the services and the bytes on disk: raw
/// journal sinks and sources, keyed by kind and id. <see cref="FileJournalStore"/>
/// is the real store; tests can substitute. Framing (JSONL), schema
/// (<see cref="JournalCodec"/>), and fold semantics live above this seam —
/// the store only moves streams. Future stores (e.g. an engine roster) slot
/// in beside the journals without touching this contract.
/// </summary>
internal interface IJournalStore
{
    /// <summary>
    /// Create the append sink for a new journal. The caller owns the stream
    /// and disposes it when the journal completes.
    /// </summary>
    /// <exception cref="IOException">A journal with this id already exists, or the sink cannot be created.</exception>
    Stream CreateJournal(JournalKind kind, string id);

    /// <summary>Every existing journal id of the given kind (unordered — callers order by journal content).</summary>
    IReadOnlyList<string> ListJournalIds(JournalKind kind);

    /// <summary>Open an existing journal for reading. The caller owns the stream.</summary>
    /// <exception cref="IOException">The journal cannot be opened.</exception>
    Stream OpenJournal(JournalKind kind, string id);
}
