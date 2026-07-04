namespace BgTournament.Protocol;

/// <summary>
/// The wire-level constants of the commit-and-reveal fair-dice scheme
/// (PROTOCOL.md, "Provably-fair dice"): the identifier that names the roll
/// derivation on the wire, and the commitment context that binds a dice key to
/// its match.
///
/// <para>Single-sourced here — in the wire-contract assembly both the server
/// (which produces the fields) and the client verifier (which checks them)
/// reference — so producer, verifier, and the golden tests all speak the exact
/// strings PROTOCOL.md pins. The derivation itself lives in BgGame_Lib
/// (<c>VerifiableDiceSource</c> / <c>DiceKey</c> / <c>DiceCommitment</c>); these
/// are only its wire-facing names.</para>
/// </summary>
public static class VerifiableDice
{
    /// <summary>
    /// The versioned identifier carried by <c>matchStarted.diceAlgorithm</c> in
    /// fair mode: an HMAC-SHA256 keystream with byte-rejection sampling (the
    /// derivation implemented by BgGame_Lib's <c>VerifiableDiceSource</c> and
    /// specified language-neutrally in PROTOCOL.md). A verifier that does not
    /// recognize this exact string cannot check the rolls and must not report
    /// that it did.
    /// </summary>
    public const string AlgorithmId = "hmac-sha256-dice-v1";

    /// <summary>
    /// The commitment context that binds a dice key to a single match:
    /// <c>bg-tournament:match:{matchId}</c>. The server commits with this exact
    /// string before the first roll, and a verifier recomputes it from the match
    /// id, so a commitment made for one match cannot be replayed for another.
    /// This format is part of the public contract (PROTOCOL.md) — a language-
    /// neutral verifier reconstructs it byte-for-byte.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="matchId"/> is null or empty.</exception>
    public static string ContextFor(string matchId)
    {
        ArgumentException.ThrowIfNullOrEmpty(matchId);
        return $"bg-tournament:match:{matchId}";
    }
}
