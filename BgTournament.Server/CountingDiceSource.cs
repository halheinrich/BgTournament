using BgGame_Lib;

namespace BgTournament.Server;

/// <summary>
/// An <see cref="IDiceSource"/> decorator that counts the rolls it has produced,
/// so the host can stamp each play query with its roll's stream position
/// (<c>playQuery.rollIndex</c>) for fair-mode verification.
///
/// <para>The index a play query carries is <see cref="RollsProduced"/> minus one,
/// read when the query is issued: <see cref="MatchRunner"/> takes exactly one
/// roll immediately before each play decision and nothing between, so the most
/// recently produced roll is the one being played. That sequencing is the
/// invariant this relies on; the fair-mode smoke re-derives the whole stream
/// from the revealed key against the transcript, which fails loudly if it ever
/// breaks.</para>
///
/// <para>The match loop is sequential — a roll and the ensuing query never run
/// concurrently — so the counter is written and read from one logical flow; the
/// interlocked update is defensive, not contended.</para>
/// </summary>
internal sealed class CountingDiceSource(IDiceSource inner) : IDiceSource
{
    private readonly IDiceSource _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private int _rollsProduced;

    /// <summary>The number of rolls produced so far. The next roll's 0-based index is this value.</summary>
    public int RollsProduced => Volatile.Read(ref _rollsProduced);

    /// <inheritdoc/>
    public (int Die1, int Die2) Roll()
    {
        var roll = _inner.Roll();
        Interlocked.Increment(ref _rollsProduced);
        return roll;
    }
}
