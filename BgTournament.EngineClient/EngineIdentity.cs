namespace BgTournament.EngineClient;

/// <summary>
/// The identity an engine presents in its handshake hello: its unique
/// registry name plus optional version and author labels.
/// </summary>
public sealed record EngineIdentity
{
    /// <summary>The engine's unique name — its server registry identity.</summary>
    public string Name { get; }

    /// <summary>Optional engine version label.</summary>
    public string? Version { get; }

    /// <summary>Optional author attribution.</summary>
    public string? Author { get; }

    /// <summary>Create an identity.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or whitespace.</exception>
    public EngineIdentity(string name, string? version = null, string? author = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Version = version;
        Author = author;
    }
}
