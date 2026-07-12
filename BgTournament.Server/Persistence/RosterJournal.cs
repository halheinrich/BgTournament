using System.Text;

namespace BgTournament.Server.Persistence;

/// <summary>
/// The roster's write path: one lazily opened segment per roster-mutating
/// server session, written <b>synchronously</b> — append, flush, return.
///
/// <para><b>Deliberately not a <see cref="JournalWriter{TEvent}"/>.</b> The
/// channel-and-pump writer exists for observer callbacks on the match loop,
/// which must never block or throw; it buys that with fire-and-forget
/// durability. Roster mutations are the opposite trade: they run on
/// admin-request threads with no latency constraint, and durability is
/// load-bearing — a registration response carries the show-once engine key,
/// so the credential must be on disk <em>before</em> the key is shown (a
/// token the roster forgot in a crash would authenticate nothing, ever).
/// A write failure therefore <em>throws</em>, failing the admin request —
/// fail-the-mutation, never fail-silent.</para>
///
/// <para>Not thread-safe: <c>RosterService</c> serializes every mutation
/// under its gate, and this type is never touched from anywhere else.</para>
/// </summary>
internal sealed class RosterJournal : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IJournalStore _store;
    private readonly TimeProvider _time;
    private StreamWriter? _writer;

    public RosterJournal(IJournalStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
    }

    /// <summary>
    /// Append one event durably: the segment (created on the first append of
    /// this session, headed by a version-stamped <see cref="RosterStartedEvent"/>)
    /// has the line flushed before this method returns.
    /// </summary>
    /// <exception cref="IOException">The event could not be persisted; the mutation must fail with it.</exception>
    public void Append(RosterJournalEvent journalEvent)
    {
        if (_writer is null)
        {
            var writer = new StreamWriter(
                _store.CreateJournal(JournalKind.Roster, Guid.NewGuid().ToString("N")), Utf8NoBom)
            {
                NewLine = "\n",
            };
            writer.WriteLine(JournalCodec.Serialize(
                new RosterStartedEvent(_time.GetUtcNow(), JournalCodec.RosterSchemaVersion)));
            writer.Flush();
            _writer = writer;
        }

        _writer.WriteLine(JournalCodec.Serialize(journalEvent));
        _writer.Flush();
    }

    /// <summary>Close the session's segment, if one was opened.</summary>
    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }
}
