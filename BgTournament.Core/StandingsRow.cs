namespace BgTournament.Core;

/// <summary>
/// One participant's line in the standings. Rows are ordered by the
/// tie-break ladder — wins, then head-to-head within the tied group, then
/// Sonneborn-Berger, then seeding (registration) order — so <see cref="Rank"/>
/// is total and deterministic.
/// </summary>
public sealed record StandingsRow
{
    /// <summary>The participant's place, 1-based; unique per row.</summary>
    public int Rank { get; }

    /// <summary>The participant's name.</summary>
    public string Participant { get; }

    /// <summary>Matches won so far.</summary>
    public int Wins { get; }

    /// <summary>Matches lost so far (forfeits count as ordinary losses).</summary>
    public int Losses { get; }

    /// <summary>
    /// The Sonneborn-Berger score on wins: the sum, over every match this
    /// participant won, of the beaten opponent's total win count. Backgammon
    /// has no draws, so the score is integral.
    /// </summary>
    public int SonnebornBerger { get; }

    /// <summary>Create a standings row.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="rank"/> is less than 1, or a count is negative.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="participant"/> is null, empty, or whitespace.
    /// </exception>
    public StandingsRow(int rank, string participant, int wins, int losses, int sonnebornBerger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rank, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(participant);
        ArgumentOutOfRangeException.ThrowIfNegative(wins);
        ArgumentOutOfRangeException.ThrowIfNegative(losses);
        ArgumentOutOfRangeException.ThrowIfNegative(sonnebornBerger);

        Rank = rank;
        Participant = participant;
        Wins = wins;
        Losses = losses;
        SonnebornBerger = sonnebornBerger;
    }
}
