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
}
