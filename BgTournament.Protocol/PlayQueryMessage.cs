namespace BgTournament.Protocol;

/// <summary>
/// Server → engine. You are on roll with these dice: choose your play. Answer
/// with <see cref="PlayReplyMessage"/>.
/// </summary>
public sealed record PlayQueryMessage : QueryMessage
{
    /// <summary>The position and match context, in your frame; you are on roll.</summary>
    public required WireGameState State { get; init; }

    /// <summary>First die, 1–6.</summary>
    public required int Die1 { get; init; }

    /// <summary>Second die, 1–6. Equal to <see cref="Die1"/> for doubles.</summary>
    public required int Die2 { get; init; }

    /// <summary>
    /// Fair-mode only: this roll's 0-based position in the match's dice stream —
    /// the ordinal of the server's roll that produced <see cref="Die1"/> /
    /// <see cref="Die2"/> (see PROTOCOL.md, "Provably-fair dice"). Every roll the
    /// server takes advances the index by one, including opening-roll re-rolls on
    /// ties and dance turns you are never queried for, so the rolls you observe
    /// are a gapped subsequence. The index lets you place each observed roll at
    /// its exact position in the committed stream and verify it once the key is
    /// revealed. Omitted for explicit-seed matches (no commitment to verify against).
    /// </summary>
    public int? RollIndex { get; init; }
}
