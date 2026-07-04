namespace BgTournament.Api;

/// <summary>
/// One board position in a replay, always expressed in <b>seat One's frame</b>
/// regardless of whose turn it is — the server normalizes every recorded
/// position, so a viewer renders each engine on a fixed side of the board and
/// never handles perspective.
/// </summary>
/// <param name="Board">
/// 26-element point array (BgDataTypes "Mop" convention), seat-One-anchored:
/// positive counts are seat One's checkers, negative seat Two's;
/// <c>[1..24]</c> are the points in seat One's numbering, <c>[25]</c> is seat
/// One's bar, <c>[0]</c> seat Two's bar.
/// </param>
/// <param name="CubeValue">Doubling-cube value (1, 2, 4, ...).</param>
/// <param name="CubeOwner">Who holds the cube, seat-keyed.</param>
public sealed record GamePosition(
    IReadOnlyList<int> Board,
    int CubeValue,
    CubeOwner CubeOwner);
