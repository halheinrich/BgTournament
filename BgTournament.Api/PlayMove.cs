namespace BgTournament.Api;

/// <summary>
/// A single checker movement inside a <see cref="PlayEntry"/>, in the
/// <b>acting player's own numbering</b> — the frame standard backgammon
/// notation uses, so a viewer prints moves verbatim. Hits are not encoded.
/// Board rendering never interprets these coordinates: each position is
/// served ready-made (the entry after this one shows the play's outcome).
/// </summary>
/// <param name="From">Source point: 1–24 in the actor's numbering, or 25 to enter from the actor's bar.</param>
/// <param name="To">Destination point: 1–24 in the actor's numbering, or 0 to bear the checker off.</param>
public sealed record PlayMove(int From, int To);
