using System.Collections.Concurrent;

namespace BgTournament.Server;

/// <summary>
/// One registered engine: its handshake identity, its live connection, and a
/// busy flag claimed for the duration of a match (v1: one match at a time per
/// engine — which is also what makes one-in-flight-query-per-engine hold).
/// </summary>
internal sealed class EngineSession
{
    private int _inMatch;

    public EngineSession(string name, string? version, string? author, EngineConnection connection)
    {
        Name = name;
        Version = version;
        Author = author;
        Connection = connection;
    }

    public string Name { get; }

    public string? Version { get; }

    public string? Author { get; }

    public EngineConnection Connection { get; }

    public bool InMatch => Volatile.Read(ref _inMatch) == 1;

    /// <summary>Atomically claim this engine for a match; false if already claimed.</summary>
    public bool TryEnterMatch() => Interlocked.CompareExchange(ref _inMatch, 1, 0) == 0;

    /// <summary>Release the match claim.</summary>
    public void ExitMatch() => Volatile.Write(ref _inMatch, 0);
}

/// <summary>
/// The connected engines, keyed by their unique handshake names. Names are
/// the identities matches are started with; a name frees up when its
/// connection ends.
/// </summary>
internal sealed class EngineRegistry
{
    private readonly ConcurrentDictionary<string, EngineSession> _engines = new(StringComparer.Ordinal);

    /// <summary>Register a session; false when the name is already connected.</summary>
    public bool TryRegister(EngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _engines.TryAdd(session.Name, session);
    }

    /// <summary>Look up a connected engine by name.</summary>
    public bool TryGet(string name, out EngineSession session) =>
        _engines.TryGetValue(name, out session!);

    /// <summary>Remove a session at connection end (only if it is still the registered one).</summary>
    public void Remove(EngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ((ICollection<KeyValuePair<string, EngineSession>>)_engines)
            .Remove(new KeyValuePair<string, EngineSession>(session.Name, session));
    }

    /// <summary>A point-in-time list of connected engines.</summary>
    public IReadOnlyList<EngineSession> Snapshot() => _engines.Values.ToArray();
}
