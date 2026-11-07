namespace BgTournament.Protocol;

/// <summary>
/// The position and match context carried by every decision query, always
/// expressed in the frame of the player on roll — positive board values are
/// the on-roll player's checkers, <see cref="WireCubeOwner.OnRoll"/> means the
/// on-roll player owns the cube. On a play or cube-offer query the engine
/// receiving it is on roll; on a cube-response query it is the opponent of the
/// player on roll, the negative side. See PROTOCOL.md ("The frame rule").
///
/// <para>The score comes in one of two kinds, told by
/// <see cref="MatchLength"/>: a match state (length &gt; 0) carries what each
/// side still needs (<see cref="OnRollNeeds"/>, <see cref="OpponentNeeds"/>),
/// a money state (length 0) the running scores (<see cref="OnRollScore"/>,
/// <see cref="OpponentScore"/>). A state carrying the other kind's fields is
/// refused (<see cref="WireMapping.ToGameSnapshot"/>).</para>
/// </summary>
public sealed record WireGameState
{
    /// <summary>
    /// 26-element board, in the on-roll player's frame. Index 0 is the
    /// opponent's bar, 1–24 are points numbered from the on-roll player's
    /// bear-off end (its home board is 1–6; it moves toward 0), 25 is the
    /// on-roll player's bar. Positive counts are the on-roll player's
    /// checkers, negative are the opponent's.
    /// </summary>
    public required IReadOnlyList<int> Board { get; init => field = ImmutableCopy.Of(value); }

    /// <summary>Current doubling-cube value: 1, 2, 4, 8, …</summary>
    public required int CubeValue { get; init; }

    /// <summary>
    /// Who may offer the next double, relative to the player on roll. In a
    /// cube-response query this reflects the pre-double cube —
    /// <see cref="WireCubeOwner.Centered"/> (first double) or
    /// <see cref="WireCubeOwner.OnRoll"/> (a redouble by the on-roll player),
    /// never <see cref="WireCubeOwner.Opponent"/>.
    /// </summary>
    public required WireCubeOwner CubeOwner { get; init; }

    /// <summary>Match length in points; 0 means a money session.</summary>
    public required int MatchLength { get; init; }

    /// <summary>Match play only: the points the player on roll still needs to win the match.</summary>
    public int? OnRollNeeds { get; init; }

    /// <summary>Match play only: the points the opponent of the player on roll still needs to win the match.</summary>
    public int? OpponentNeeds { get; init; }

    /// <summary>Money play only: the points the player on roll has won so far this session.</summary>
    public int? OnRollScore { get; init; }

    /// <summary>Money play only: the points the opponent of the player on roll has won so far this session.</summary>
    public int? OpponentScore { get; init; }

    /// <summary>True iff the current game is the Crawford game (cube suspended).</summary>
    public required bool IsCrawford { get; init; }

    /// <summary>
    /// Optional debug decoration: the position's XGID. The server MAY include
    /// it; engines MUST NOT rely on it — <see cref="Board"/> and the match
    /// fields are the contract.
    /// </summary>
    public string? Xgid { get; init; }
}
