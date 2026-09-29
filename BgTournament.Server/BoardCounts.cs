using BgDataTypes_Lib;

namespace BgTournament.Server;

/// <summary>
/// A position's counts as the 26-element list the server's board-carrying
/// shapes hold — the journal's and the admin Api's. One conversion, so every
/// such shape reads a <see cref="BoardPosition"/> the same way, in the
/// substrate's layout and frame.
/// </summary>
internal static class BoardCounts
{
    /// <summary>The slots of the substrate's board layout: both bars and the 24 points.</summary>
    private const int Slots = 26;

    /// <summary>The 26 counts of <paramref name="position"/>, in its layout and frame.</summary>
    public static int[] Of(BoardPosition position)
    {
        var counts = new int[Slots];
        position.CopyTo(counts);
        return counts;
    }
}
