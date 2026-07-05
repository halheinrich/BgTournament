using System.Text.Json;
using System.Text.Json.Serialization;

namespace BgTournament.Server.Persistence;

/// <summary>
/// The journal format definition: its schema version constant and the single
/// serialization boundary for journal events (the persistence counterpart of
/// <c>WireProtocol</c>).
///
/// <para>All journal (de)serialization goes through here. Deliberate
/// encapsulation, not convenience: System.Text.Json emits the <c>"type"</c>
/// discriminator only when the declared type is the polymorphic base, so
/// serializing an event through its concrete type would silently drop the
/// discriminator and write a line no rehydration can dispatch.</para>
///
/// <para>The format is durable: files written today must fold forever.
/// <b>Any</b> change to the emitted bytes — a field, an enum string, a
/// discriminator — is a schema change: bump <see cref="SchemaVersion"/> and
/// decide the migration story consciously (JournalGoldenTests is the alarm).</para>
/// </summary>
internal static class JournalCodec
{
    /// <summary>
    /// The journal schema version this server writes. Carried once per file,
    /// on the header (first) event; rehydration rejects a file whose version
    /// it does not know before folding anything from it.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// Journal conventions: camelCase properties, enums as the strings pinned
    /// per member, optional fields omitted when null (additive-friendly for a
    /// durable format), discriminator accepted in any position on read.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
    };

    /// <summary>Serialize a match-journal event to its JSONL line (one JSON object, no indentation).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="journalEvent"/> is null.</exception>
    public static string Serialize(MatchJournalEvent journalEvent)
    {
        ArgumentNullException.ThrowIfNull(journalEvent);
        return JsonSerializer.Serialize(journalEvent, Options);
    }

    /// <summary>Serialize a tournament-journal event to its JSONL line (one JSON object, no indentation).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="journalEvent"/> is null.</exception>
    public static string Serialize(TournamentJournalEvent journalEvent)
    {
        ArgumentNullException.ThrowIfNull(journalEvent);
        return JsonSerializer.Serialize(journalEvent, Options);
    }

    /// <summary>
    /// Deserialize one match-journal line. Any malformed line — invalid JSON,
    /// JSON <c>null</c>, a missing or unknown <c>"type"</c>, a missing
    /// required field, or an out-of-range enum string — throws
    /// <see cref="JsonException"/>, the single failure type the rehydrator's
    /// torn-tail and corruption policies key on.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    /// <exception cref="JsonException">The line is not a well-formed match-journal event.</exception>
    public static MatchJournalEvent DeserializeMatchEvent(string line) =>
        Deserialize<MatchJournalEvent>(line);

    /// <summary>
    /// Deserialize one tournament-journal line; the same single
    /// <see cref="JsonException"/> funnel as <see cref="DeserializeMatchEvent"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    /// <exception cref="JsonException">The line is not a well-formed tournament-journal event.</exception>
    public static TournamentJournalEvent DeserializeTournamentEvent(string line) =>
        Deserialize<TournamentJournalEvent>(line);

    private static TEvent Deserialize<TEvent>(string line) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(line);
        try
        {
            return JsonSerializer.Deserialize<TEvent>(line, Options)
                ?? throw new JsonException("The journal line is JSON null.");
        }
        catch (NotSupportedException ex)
        {
            // STJ reports a missing discriminator on an abstract base as
            // NotSupportedException; fold it into the single failure type.
            throw new JsonException("The journal line is not a recognized journal event.", ex);
        }
    }
}
