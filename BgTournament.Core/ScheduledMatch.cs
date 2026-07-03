namespace BgTournament.Core;

/// <summary>
/// One match in a tournament's schedule: which participant takes which seat,
/// and the dice seed the match must be played with.
/// </summary>
public sealed record ScheduledMatch
{
    /// <summary>The match's position in the schedule (0-based, contiguous).</summary>
    public int Index { get; }

    /// <summary>The participant playing seat one.</summary>
    public string SeatOne { get; }

    /// <summary>The participant playing seat two.</summary>
    public string SeatTwo { get; }

    /// <summary>
    /// The dice seed for this match, derived deterministically from the
    /// tournament seed and <see cref="Index"/>. Distinct per match by
    /// construction intent — identical seeds across a pairing's matches would
    /// replay the same game against deterministic engines and defeat playing
    /// the pairing more than once.
    /// </summary>
    public int Seed { get; }

    /// <summary>Create a scheduled match.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    /// <exception cref="ArgumentException">
    /// A seat name is null, empty, or whitespace, or the two seats name the same participant.
    /// </exception>
    public ScheduledMatch(int index, string seatOne, string seatTwo, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(seatOne);
        ArgumentException.ThrowIfNullOrWhiteSpace(seatTwo);
        if (string.Equals(seatOne, seatTwo, StringComparison.Ordinal))
        {
            throw new ArgumentException("A match needs two distinct participants.", nameof(seatTwo));
        }

        Index = index;
        SeatOne = seatOne;
        SeatTwo = seatTwo;
        Seed = seed;
    }
}
