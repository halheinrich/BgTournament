namespace BgTournament.Protocol;

/// <summary>
/// The position and match context carried by every decision query, always
/// expressed in the frame of the engine receiving it — positive board values
/// are your checkers, <see cref="YourScore"/> is your score,
/// <see cref="WireCubeOwner.You"/> means you own the cube. See PROTOCOL.md
/// ("The frame rule") for the full convention, including the one place the
/// on-roll player is not you: a cube-response query, where your opponent
/// doubled and rolls next if you take.
/// </summary>
public sealed record WireGameState
{
    /// <summary>
    /// 26-element board. Index 0 is your opponent's bar, 1–24 are points
    /// numbered from your bear-off end (your home board is 1–6; you move
    /// toward 0), 25 is your bar. Positive counts are your checkers, negative
    /// are your opponent's.
    /// </summary>
    public required IReadOnlyList<int> Board { get; init; }

    /// <summary>Current doubling-cube value: 1, 2, 4, 8, …</summary>
    public required int CubeValue { get; init; }

    /// <summary>
    /// Who may offer the next double. In a cube-response query this reflects
    /// the pre-double cube — <see cref="WireCubeOwner.Centered"/> (first
    /// double) or <see cref="WireCubeOwner.Opponent"/> (redouble), never
    /// <see cref="WireCubeOwner.You"/>.
    /// </summary>
    public required WireCubeOwner CubeOwner { get; init; }

    /// <summary>Match length in points; 0 means a money session.</summary>
    public required int MatchLength { get; init; }

    /// <summary>Points you have scored in this match so far.</summary>
    public required int YourScore { get; init; }

    /// <summary>Points your opponent has scored in this match so far.</summary>
    public required int OpponentScore { get; init; }

    /// <summary>True iff the current game is the Crawford game (cube suspended).</summary>
    public required bool IsCrawford { get; init; }

    /// <summary>
    /// Optional debug decoration: the position's XGID. The server MAY include
    /// it; engines MUST NOT rely on it — <see cref="Board"/> and the match
    /// fields are the contract.
    /// </summary>
    public string? Xgid { get; init; }
}
