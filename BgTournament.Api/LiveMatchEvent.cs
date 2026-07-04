using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// One event on the live per-move feed of <c>GET /matches/{matchId}/live</c>
/// (an <c>text/event-stream</c>). Discriminated on the wire by <c>"type"</c>
/// (<c>snapshot</c> / <c>gameStarted</c> / <c>entry</c> / <c>gameEnded</c> /
/// <c>terminal</c>) — the same self-describing discriminated-union style the
/// replay entries and the engine wire use, so a consumer deserializes to this
/// base with plain Web defaults and no dependence on the SSE framing. The
/// payloads reuse the settled replay contracts (<see cref="GameEntry"/>,
/// <see cref="GameReplay"/>) and the match summary (<see cref="MatchSummary"/>);
/// only these envelope shapes are feed-specific.
///
/// <para><b>Sequence.</b> A subscriber first receives exactly one
/// <see cref="LiveSnapshotEvent"/> (its join-in-progress state), then a live
/// stream of <see cref="LiveGameStartedEvent"/> / <see cref="LiveEntryEvent"/> /
/// <see cref="LiveGameEndedEvent"/> increments, and finally exactly one
/// <see cref="LiveTerminalEvent"/>, after which the server closes the stream.
/// A subscriber that joins an already-finished match receives its snapshot and
/// terminal immediately, then close.</para>
///
/// <para><b>No resume.</b> Version 1 sends no event ids and honors no
/// <c>Last-Event-ID</c>: a dropped connection is recovered by re-subscribing,
/// whose fresh snapshot re-establishes state — replay-by-id is unnecessary.</para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LiveSnapshotEvent), "snapshot")]
[JsonDerivedType(typeof(LiveGameStartedEvent), "gameStarted")]
[JsonDerivedType(typeof(LiveEntryEvent), "entry")]
[JsonDerivedType(typeof(LiveGameEndedEvent), "gameEnded")]
[JsonDerivedType(typeof(LiveTerminalEvent), "terminal")]
public abstract record LiveMatchEvent;

/// <summary>
/// The join-in-progress state, sent once to every new subscriber: the current
/// game's number, the running match score entering it, and the decision
/// entries recorded so far. A subscriber that joins before any move is queued
/// gets an empty <paramref name="Entries"/>; one that joins a finished match
/// gets the last game's context. Completed games are not carried here — they
/// are reachable through <c>GET /matches/{matchId}/games</c>.
/// </summary>
/// <param name="GameNumber">1-based number of the game currently in view.</param>
/// <param name="SeatOneScore">Seat One's match score entering the current game.</param>
/// <param name="SeatTwoScore">Seat Two's match score entering the current game.</param>
/// <param name="IsCrawford">True iff the game currently in view is the Crawford game.</param>
/// <param name="Entries">The current game's decision moments so far, in play order.</param>
public sealed record LiveSnapshotEvent(
    int GameNumber,
    int SeatOneScore,
    int SeatTwoScore,
    bool IsCrawford,
    IReadOnlyList<GameEntry> Entries) : LiveMatchEvent;

/// <summary>
/// A new game is starting. Fires before the opening roll — no board yet (the
/// substrate provides none until the opening play) — so it carries only the
/// frame-free game context: the game number, the running score entering it, and
/// whether it is the Crawford game, for a between-games display.
/// </summary>
/// <param name="GameNumber">1-based number of the game starting.</param>
/// <param name="SeatOneScore">Seat One's match score entering the game.</param>
/// <param name="SeatTwoScore">Seat Two's match score entering the game.</param>
/// <param name="IsCrawford">True iff the game starting is the Crawford game.</param>
public sealed record LiveGameStartedEvent(
    int GameNumber,
    int SeatOneScore,
    int SeatTwoScore,
    bool IsCrawford) : LiveMatchEvent;

/// <summary>
/// One decision moment appended to the current game — the same
/// <see cref="GameEntry"/> shape a settled replay serves, delivered live as it
/// is recorded. The terminating game-end is not an entry; it arrives as
/// <see cref="LiveGameEndedEvent"/>.
/// </summary>
/// <param name="Entry">The play / cube-offer / cube-response just recorded.</param>
public sealed record LiveEntryEvent(GameEntry Entry) : LiveMatchEvent;

/// <summary>
/// The current game finished. Carries the game's canonical
/// <see cref="GameReplay"/> — outcome, entering scores, every entry, and the
/// terminal position — identical to what <c>GET /matches/{matchId}/games</c>
/// would serve for it, so a live viewer needs no follow-up fetch to finalize.
/// </summary>
/// <param name="Game">The completed game, replay-ready.</param>
public sealed record LiveGameEndedEvent(GameReplay Game) : LiveMatchEvent;

/// <summary>
/// The match reached a terminal state — completed, forfeited, aborted, or
/// faulted. Carries the final <see cref="MatchSummary"/> (the authoritative
/// record: status, winner, scores, and any forfeit detail); it is the last
/// event before the server closes the stream.
/// </summary>
/// <param name="Match">The final match record.</param>
public sealed record LiveTerminalEvent(MatchSummary Match) : LiveMatchEvent;
