namespace BgTournament.Core;

/// <summary>
/// A round-robin tournament's configuration: every pair of participants plays
/// <see cref="MatchesPerPairing"/> matches, each to <see cref="MatchLength"/>
/// points, and every match counts individually toward the standings.
/// </summary>
public sealed record TournamentFormat
{
    /// <summary>
    /// Points a match is played to. At least 1 — every tournament match must
    /// produce a winner, so money sessions (length 0) are not a tournament
    /// format.
    /// </summary>
    public int MatchLength { get; }

    /// <summary>
    /// Matches each pair of participants plays. Every match counts toward the
    /// standings individually (the chess convention) — this is not a
    /// best-of-K that decides one pairing winner. An even value balances the
    /// seat assignment exactly K/2 each way across a pairing.
    /// </summary>
    public int MatchesPerPairing { get; }

    /// <summary>Create a format.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="matchLength"/> or <paramref name="matchesPerPairing"/> is less than 1.
    /// </exception>
    public TournamentFormat(int matchLength, int matchesPerPairing)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(matchLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(matchesPerPairing, 1);
        MatchLength = matchLength;
        MatchesPerPairing = matchesPerPairing;
    }
}
