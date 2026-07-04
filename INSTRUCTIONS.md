# BgTournament — Subproject Instructions

> Collaboration contract: [`../AGENTS.md`](../AGENTS.md)
> Umbrella status & dependency graph: [`../INSTRUCTIONS.md`](../INSTRUCTIONS.md)
> Mission & principles: [`../VISION.md`](../VISION.md)

## Stack

C# / .NET 10 — ASP.NET Core (Kestrel + WebSockets) server, three class
libraries, xUnit test project (`Microsoft.AspNetCore.Mvc.Testing` for in-proc
wire tests). Visual Studio 2026, Windows.

## Solution

`D:\Users\Hal\Documents\Visual Studio 2026\Projects\backgammon\BgTournament\BgTournament.slnx`

## Repo

https://github.com/halheinrich/BgTournament — branch `main`.

## Depends on

- **BgDataTypes_Lib** — `Play`/`Move` (sign-encoded hits, `DeduplicationKey`
  equality), `BoardState.FromMop`, `CubeAction`/`CubeOwner`.
- **BgGame_Lib** — `IPlayAgent`/`ICubeAgent` (the unified queried-player-frame
  contract), `GameState` (`FromPosition`, `OpponentView`, `Snapshot`),
  `MatchState.FromScores`, `GameSnapshot`/`MatchSnapshot`, `MatchRunner` +
  `SeededDiceSource`, `AgentContractViolationException` (public ctors are the
  adapter's forfeit hook), transcripts.
- **BgMoveGen** — `MoveGenerator.GeneratePlays`: the server's legality
  universe (`PlayResolver`) and the reference bot's candidate set.
- **BgInference** (test-only) — the dogfooding smoke's real engine
  (`OnePlyPlayAgent` + `ThresholdCubeAgent` over BgRLEngine's parity model,
  read in place from the sibling checkout). Not a dependency of the shipped
  projects.

## Directory tree

```
BgTournament/
├── BgTournament.slnx
├── Directory.Packages.props            CPM — inline Version= is banned
├── PROTOCOL.md                         THE engine contract (language-neutral, versioned)
├── INSTRUCTIONS.md
├── BgTournament.Protocol/              wire contract's .NET binding + substrate bridge
│   ├── ProtocolMessage.cs              polymorphic base; "type" discriminator registry
│   ├── QueryMessage.cs / ReplyMessage.cs   requestId-carrying bases
│   ├── HelloMessage.cs / WelcomeMessage.cs / RejectedMessage.cs
│   ├── PlayQueryMessage.cs / PlayReplyMessage.cs
│   ├── CubeOfferQueryMessage.cs / CubeOfferReplyMessage.cs
│   ├── CubeResponseQueryMessage.cs / CubeResponseReplyMessage.cs
│   ├── MatchStartedMessage.cs / MatchEndedMessage.cs
│   ├── WireGameState.cs / WireMove.cs  hand-defined wire shapes (never substrate types)
│   ├── WireCubeOwner.cs / CubeOfferAction.cs / CubeResponseAction.cs
│   ├── MatchEndReason.cs / ForfeitSide.cs
│   ├── WireProtocol.cs                 Version const + the ONLY (de)serialization path
│   ├── WireMapping.cs                  the ONLY wire ↔ substrate field correspondences
│   └── ProtocolSocket.cs               framing rule (1 text frame = 1 message, 64 KiB)
├── BgTournament.Api/                   admin HTTP contracts (zero dependencies, public)
│   ├── MatchStatus.cs / TournamentStatus.cs    status vocabularies (Server uses them too)
│   ├── ErrorResponse.cs                the typed error body on every non-success response
│   ├── StartMatchRequest.cs / StartTournamentRequest.cs
│   ├── EngineSummary.cs / MatchSummary.cs
│   ├── StandingEntry.cs / TournamentMatchEntry.cs / TournamentSummary.cs
│   ├── Seat.cs / CubeOwner.cs          seat-keyed identities for replay shapes
│   ├── GameResultKind.cs / CubeResponseAction.cs
│   ├── GamePosition.cs / PlayMove.cs   seat-One-frame position; mover-relative move
│   ├── GameEntry.cs                    "type"-discriminated union: play/cubeOffer/cubeResponse
│   ├── GameReplay.cs / MatchGamesResponse.cs   per-game replay + the endpoint envelope
│   └── LiveMatchEvent.cs               live-feed envelope union: snapshot/gameStarted/entry/gameEnded/terminal
├── BgTournament.Core/                  execution-blind tournament domain (zero dependencies)
│   ├── TournamentFormat.cs             round-robin config: matchLength × matchesPerPairing
│   ├── ScheduledMatch.cs               one schedule row: seats + the derived dice seed
│   ├── StandingsRow.cs                 one standings line: wins, losses, Sonneborn-Berger
│   └── Tournament.cs                   the aggregate: schedule, results, tie-break ladder
├── BgTournament.Server/                the tournament host (all types internal)
│   ├── Program.cs                      /engine (WS) + the admin HTTP endpoints
│   ├── EngineSocketEndpoint.cs         handshake gate; named rejections
│   ├── EngineConnection.cs             receive loop; one in-flight query; Closed task
│   ├── IEngineChannel.cs               query seam (EngineConnection live, faked in tests)
│   ├── RemoteEngineAgent.cs            IPlayAgent+ICubeAgent over the channel; taxonomy
│   ├── PlayResolver.cs                 wire play → canonical hit-encoded candidate
│   ├── EngineRegistry.cs               sessions by name; per-engine busy flag
│   ├── MatchService.cs                 match host: attribution, forfeits, records
│   ├── TournamentService.cs            tournament host: claims, orchestration, folding
│   ├── EngineFailureExceptions.cs      timeout / disconnected / protocol-violation
│   ├── TournamentOptions.cs            decision + handshake timeouts (appsettings)
│   ├── ApiMapping.cs                   the ONLY server-internal → Api projections
│   ├── ReplayProjection.cs             transcript → replay contract: stamped-seat read + flip
│   └── LiveMatch.cs                    per-match live cache + SSE broadcast (the IMatchObserver adapter)
├── BgTournament.EngineClient/          .NET SDK + reference bot
│   ├── EngineClient.cs                 connect/handshake/serve loop over local agents
│   ├── EngineIdentity.cs               hello identity
│   ├── HandshakeRejectedException.cs
│   ├── RandomPlayAgent.cs              reference play policy (seed required)
│   └── PassiveCubeAgent.cs             reference cube policy (never double, always take)
└── BgTournament.Tests/
    ├── GoldenWireTests.cs              byte-for-byte wire pins, every message
    ├── ApiGoldenTests.cs               byte-for-byte admin JSON pins, every shape + enum
    ├── ProtocolRoundTripTests.cs       strictness + tolerance edges
    ├── ProtocolDocTests.cs             PROTOCOL.md examples stay canonical wire text
    ├── WireMappingTests.cs             field preservation + the no-double-flip pin
    ├── ReferenceBotTests.cs            legality sweep, determinism, empty play
    ├── PlayResolverTests.cs            hit canonicalization + dance resolution
    ├── RemoteEngineAgentTests.cs       forfeit taxonomy over a scripted channel
    ├── ServerTestHarness.cs            TestEngine (raw-wire scripting) + helpers
    ├── ServerIntegrationTests.cs       handshake gate + taxonomy over TestServer
    ├── WireMatchSmokeTests.cs          full wire matches: random pair + BgInference
    ├── ListEndpointTests.cs            /matches + /tournaments listings, creation order
    ├── ReplayProjectionTests.cs        the stamped-seat projection on scripted-dice MatchRunner runs
    ├── ReplayEndpointTests.cs          /matches/{id}/games over TestServer, 404/409/partial
    ├── LiveMatchTests.cs               the live cache/broadcast core over its IMatchObserver surface
    ├── LiveEndpointTests.cs            /matches/{id}/live SSE end to end: ordering, forfeit, already-done
    ├── TournamentCoreTests.cs          domain pins: schedule, seeds, tie-break ladder
    ├── TournamentServerTests.cs        tournament claims, validation, forfeit folding
    └── TournamentSmokeTests.cs         3-engine round-robin over the wire to a winner
```

## Architecture

**What this is.** The computer-tournament arena: a versioned,
language-neutral WebSocket+JSON protocol that *is* the engine contract
(`PROTOCOL.md`), an ASP.NET Core server that adapts each connected engine
onto `IPlayAgent`/`ICubeAgent` so BgGame_Lib's `MatchRunner` referees wire
matches without knowing sockets exist — and hosts round-robin tournaments
over that same machinery — and a .NET client SDK that turns any in-proc
agent pair into a remote engine. Server-authoritative throughout: the server
owns dice (seeded, always recorded), legality, state, pairing, and
refereeing; engines only answer decision queries.

**The JSON is the contract.** `PROTOCOL.md` is the SSOT for wire semantics;
the `BgTournament.Protocol` assembly is its .NET binding. Three enforcement
layers keep them honest: golden tests pin every message's exact wire text,
`ProtocolDocTests` re-serializes every fenced example in `PROTOCOL.md`
byte-for-byte, and `TreatWarningsAsErrors` XML-doc coverage keeps the DTOs
self-describing. `WireProtocol` is the only (de)serialization path — a
deliberate encapsulation of the System.Text.Json footgun where serializing
through a concrete declared type silently drops the `"type"` discriminator.
All malformed-frame failures fold into a single `JsonException` funnel that
adapters translate into protocol violations. Reply enums are deliberately
narrow per query (`CubeOfferAction` vs `CubeResponseAction`), so an
out-of-contract action fails at the deserialization boundary, not in game
logic.

**The frame rule.** The queried player always sees its own frame — in-proc
and on the wire, one convention (the substrate re-contract this arc built on).
`WireMapping` is the only home of wire↔substrate field correspondences and is
strictly frame-preserving: `GameState.OpponentView` (substrate-side) is the
single re-expression anywhere in the system, and the no-double-flip pin fails
if a flip ever creeps into the mapping. In a cube-response query the state
shows the pre-double cube (`centered` or `opponent`, never `you`).

**Play encoding and canonicalization.** Wire moves are `{from, to}` with hits
deliberately not encoded. `WireMapping.ToUnresolvedPlay` reconstructs a
hit-less `Play` that is safe only for legality matching (`DeduplicationKey`
ignores hit signs) — `PlayResolver` resolves it to the generator's canonical
hit-encoded candidate, which is what gets applied and recorded. An unmatched
play is handed to `MatchRunner` verbatim so the legality verdict stays
single-sourced in the runner.

**Server shape.** `EngineSocketEndpoint` gates the handshake (version,
non-empty unique name; every rejection is a named `rejected` message).
`EngineConnection` runs one receive loop per engine, enforces strictly one
in-flight query (id-correlated), serializes sends, tolerates exactly the
protocol's benign race (a reply to the most recently abandoned query is
discarded), and closes the connection on any violation; its `Closed` task is
the composition point for proactive disconnect handling. `RemoteEngineAgent`
carries the forfeit taxonomy: malformed/out-of-range replies →
`AgentContractViolationException` (decision-appropriate kind + seat, public
ctors); timeout/disconnect → engine-name-carrying server exceptions; external
cancellation passes untranslated. `MatchService` owns what the substrate
deliberately does not: seat↔engine attribution, the v1 forfeit policy
(forfeit the match), the proactive disconnect watch (first connection to end
mid-match takes the forfeit — even while its opponent is thinking), per-receiver
`matchStarted`/`matchEnded` framing, and in-memory record retention (full
`MatchResult` with transcripts for completed matches). Aborted/Faulted matches
end silently on the wire — v1 has no vocabulary for them and never lies about
a reason.

**Tournament shape.** One seam, mirrored one layer up from
MatchRunner ↔ MatchService: `BgTournament.Core` is an execution-blind,
zero-dependency domain library — `Tournament` owns the round-robin schedule
(every pair meets `MatchesPerPairing` times, seats alternating within a
pairing so an even K balances the opening-roll seat exactly; each match's
dice seed is a SplitMix64 derivation of tournament seed + match index, so a
pairing's matches diverge against deterministic engines while the whole
tournament replays from one seed) and folds winners into standings (wins →
head-to-head within the tied group → Sonneborn-Berger on wins → seeding
order, a total deterministic order). `TournamentService` owns execution: it
claims every participant for the tournament's duration (which is what keeps
one-in-flight-query-per-engine an invariant across rounds), plays the
schedule strictly in order through `MatchService`'s hosting core
(`CreateHostedMatch` + `RunHostedMatchAsync` — claim-free; the standalone
`POST /matches` path wraps the same core with per-match claims), and reports
each outcome as a plain winner — a forfeit is a win for the non-offender,
and a participant found disconnected when its match comes up forfeits
without play (no wire traffic — no `matchStarted` was ever sent). A match
that ends with no winner to fold (Aborted/Faulted) halts the tournament with
that status. Tournaments are invisible on the wire: engines see only the
usual per-match lifecycle, and PROTOCOL.md stays at v1.

**Admin API shape.** `BgTournament.Api` is the second public contract (the
first is the wire): a zero-dependency assembly holding every shape that
crosses the admin HTTP boundary, referenced by the Server and by consumers
(BgArena_Blazor). The contracts are self-describing — every enum pins its
wire string per member (`JsonStringEnumMemberName`), so hosts and consumers
serialize with plain Web defaults and zero converter configuration.
`MatchStatus`/`TournamentStatus` are simultaneously the server's internal
state and the public vocabulary — one enum, so an internal status refactor is
visibly a contract change. Server-side, `ApiMapping` is the only home of
server-internal → Api projections (the admin counterpart of `WireMapping`),
and `ApiGoldenTests` pins every shape's exact JSON the way the wire goldens
pin the protocol. Listings (`GET /matches`, `GET /tournaments`) serve
creation order via a never-serialized monotonic `Sequence` on each record —
the concurrent dictionaries have no order of their own.

**Replay shape.** `GET /matches/{matchId}/games` projects a Completed match's
retained transcripts onto the replay contract (`ReplayProjection`). The
substrate records each decision in the on-roll player's frame but now stamps
every entry with its `OnRollSeat` and derives attribution on the subtypes
(`CubeTranscriptEntry.ActingSeat`, `GameEndedTranscriptEntry.Winner`), so the
projection does two host-side jobs. (1) *Seat attribution*: a plain read of the
stamped facts — no sequencing heuristics. (2) *Frame normalization*: every
position is re-expressed in seat One's frame before it crosses the API —
viewers anchor each engine to one side of the board and never learn the frame
rule; the flip round-trips through the substrate's own `GameState.OpponentView`
(the system's single re-expression), never a local mirror. Play moves stay
mover-relative on purpose — that is the frame standard backgammon notation uses
— and a viewer steps through served positions rather than applying moves. The
terminal transcript event is not an entry: outcome lives at game level and the
last position becomes `finalState`. Only a running match answers 409 (the live
feed is its surface); every terminal match serves its retained games — the full
set for a Completed one, and the games that finished before the break
(retained on `LiveMatch`) for a forfeited/aborted/faulted one — with the
qualifying `MatchStatus` on the response so a partial list is self-describing.

**Live feed shape.** `GET /matches/{matchId}/live` is a `text/event-stream`
push of one match as it plays, for the same viewers. `LiveMatch` is the
per-match cache and broadcast hub: an `IMatchObserver` (BgGame_Lib's per-move
seam) written from the runner's flow and read concurrently by SSE subscribers.
It is *non-disruptive by construction* — the substrate invokes observer
callbacks synchronously on the match loop and fails the run if one throws, so
every callback does only fast in-memory work under a short, essentially
uncontended lock and enqueues to lock-free channels; it never awaits, blocks,
or does I/O. A projection bug cannot take the match down: callbacks are
contained, and a failure *faults the feed loudly* (subscriber streams close, a
loud EOF) rather than silently stale. Subscribing is atomic snapshot-plus-
register, so a late joiner's join-in-progress snapshot and its live increments
neither gap nor duplicate; an already-terminal match takes the same one path
(snapshot → terminal → close). The event payloads are the very replay
contracts above (`GameEntry` / `GameReplay`) projected identically — the
single projection SSOT feeds both surfaces — under a small envelope union
(`LiveMatchEvent`). The one terminal event is emitted by the host
(`MatchService`, after the outcome is folded into the record), not the
substrate — which emits nothing on abort — so it fires once for every outcome
(completed / forfeited / aborted / faulted) and carries the truth. The
collected completed games are also the retained replay source, so a
forfeited/faulted match keeps the games that finished before the break.
Spectating is invisible on the wire: PROTOCOL.md stays at v1, engines see
nothing.

**Client shape.** `EngineClient.ServeAsync(WebSocket)` is the transport seam
(any socket source, in-proc test servers included); `RunAsync(Uri)` wraps it
with a `ClientWebSocket`. No perspective work client-side — the unified frame
rule means query states map straight onto the local agent contract. A local
agent exception drops the connection, which the server correctly reads as a
forfeit.

## Public API

```csharp
// BgTournament.Protocol — the wire contract's .NET binding
public static class WireProtocol
{
    public const int Version = 1;
    public static string Serialize(ProtocolMessage message);
    public static ProtocolMessage Deserialize(string json);   // JsonException on any malformed frame
}

public abstract record ProtocolMessage;                        // + 11 sealed message records:
// HelloMessage, WelcomeMessage, RejectedMessage,
// PlayQueryMessage/PlayReplyMessage, CubeOfferQueryMessage/CubeOfferReplyMessage,
// CubeResponseQueryMessage/CubeResponseReplyMessage, MatchStartedMessage, MatchEndedMessage
public abstract record QueryMessage : ProtocolMessage  { public required string RequestId; }
public abstract record ReplyMessage : ProtocolMessage  { public required string RequestId; }

public sealed record WireGameState  // board[26] your-frame, cubeValue, cubeOwner,
                                    // matchLength, yourScore, opponentScore, isCrawford, xgid?
public sealed record WireMove       { public required int From; public required int To; }
public enum WireCubeOwner { You, Opponent, Centered }
public enum CubeOfferAction { NoDouble, Double }
public enum CubeResponseAction { Take, Pass }
public enum MatchEndReason { MatchComplete, GamesCapReached, Forfeit }
public enum ForfeitSide { You, Opponent }

public static class WireMapping     // the ONLY wire ↔ substrate bridge, frame-preserving
{
    public static WireGameState ToWireState(this GameSnapshot snapshot);
    public static GameState ToGameState(this WireGameState state);
    public static IReadOnlyList<WireMove> ToWireMoves(this Play play);
    public static Play ToUnresolvedPlay(this IReadOnlyList<WireMove> moves); // NOT safe to apply
}

public sealed class ProtocolSocket  // 1 complete text frame = 1 message; 64 KiB cap
{
    public const int MaxMessageBytes;
    public ProtocolSocket(WebSocket socket);                   // wraps, does not own
    public Task SendAsync(ProtocolMessage message, CancellationToken ct = default);
    public Task<ProtocolMessage?> ReceiveAsync(CancellationToken ct = default);  // null on close
}

// BgTournament.EngineClient — the .NET SDK (a convenience, not the contract)
public sealed class EngineClient
{
    public EngineClient(EngineIdentity identity, IPlayAgent playAgent, ICubeAgent cubeAgent,
                        ILogger<EngineClient>? logger = null);
    public Task RunAsync(Uri serverUri, CancellationToken ct = default);
    public Task ServeAsync(WebSocket socket, CancellationToken ct = default);
}
public sealed record EngineIdentity   // Name / Version? / Author?; ctor validates non-empty name
{
    public EngineIdentity(string name, string? version = null, string? author = null);
}
public sealed class HandshakeRejectedException : Exception { public string Reason; }
public sealed class RandomPlayAgent : IPlayAgent   { public RandomPlayAgent(int seed); }
public sealed class PassiveCubeAgent : ICubeAgent  { }         // never doubles, always takes

// BgTournament.Core — the execution-blind tournament domain: zero dependencies,
// fully unit-testable, never runs a match. Not thread-safe (hosts serialize).
public sealed record TournamentFormat
{
    public TournamentFormat(int matchLength, int matchesPerPairing);  // both ≥ 1; every match counts
    public int MatchLength { get; }
    public int MatchesPerPairing { get; }
}
public sealed record ScheduledMatch    // Index, SeatOne, SeatTwo, Seed — one schedule row
public sealed record StandingsRow      // Rank (1-based, unique), Participant, Wins, Losses, SonnebornBerger
public sealed class Tournament
{
    public Tournament(IReadOnlyList<string> participants, TournamentFormat format, int seed);
    public IReadOnlyList<string> Participants { get; }      // seeding order — the final tie-break
    public TournamentFormat Format { get; }
    public int Seed { get; }
    public IReadOnlyList<ScheduledMatch> Schedule { get; }  // eager; pairing-major; seats alternate
    public bool IsComplete { get; }
    public string? Winner { get; }                          // null until complete
    public string? ResultOf(int matchIndex);
    public void RecordResult(int matchIndex, string winner);  // forfeit = ordinary win for the other side
    public IReadOnlyList<StandingsRow> ComputeStandings();  // wins → head-to-head → Sonneborn-Berger → seeding
}

// BgTournament.Api — the admin HTTP contract: zero dependencies, pure shapes.
// Every request/response the admin surface speaks, one record per shape,
// string enums pinned per member — consumers deserialize with Web defaults.
public sealed record ErrorResponse(string Error);          // the body of every non-success response
public sealed record StartMatchRequest(...);               // POST /matches
public sealed record StartTournamentRequest(...);          // POST /tournaments
public sealed record EngineSummary(...);                   // GET /engines rows
public sealed record MatchSummary(...);                    // match endpoints' projection
public sealed record TournamentSummary(...);               // tournament endpoints' projection
public sealed record StandingEntry(...);                   //   one standings line
public sealed record TournamentMatchEntry(...);            //   one schedule-ledger row
public enum MatchStatus { Running, Completed, Forfeited, Aborted, Faulted }
public enum TournamentStatus { Running, Completed, Aborted, Faulted }

// Replay (GET /matches/{matchId}/games) — positions are absolute:
public enum Seat { One, Two }                               // "seatOne"/"seatTwo"; One = engineOne
public enum CubeOwner { Centered, SeatOne, SeatTwo }
public enum GameResultKind { Single, Gammon, Backgammon }
public enum CubeResponseAction { Take, Pass }
public sealed record MatchGamesResponse(                    // the endpoint envelope
    string MatchId, string EngineOne, string EngineTwo, int MatchLength,
    MatchStatus Status,                                     // Completed = full; else a partial list
    IReadOnlyList<GameReplay> Games);
public sealed record GameReplay(                            // one game, replay-ready
    int GameNumber, Seat Winner, GameResultKind ResultKind, int CubeValue, int Points,
    int SeatOneScore, int SeatTwoScore, bool IsCrawford,    // scores entering the game
    IReadOnlyList<GameEntry> Entries, GamePosition FinalState);
public abstract record GameEntry(Seat Actor, GamePosition State);  // "type"-discriminated:
public sealed record PlayEntry(..., int Die1, int Die2, IReadOnlyList<PlayMove> Moves);  // "play"
public sealed record CubeOfferEntry(...);                                                // "cubeOffer"
public sealed record CubeResponseEntry(..., CubeResponseAction Action);                  // "cubeResponse"
public sealed record GamePosition(IReadOnlyList<int> Board, int CubeValue, CubeOwner CubeOwner);
    // Board: 26-element Mop array in seat One's frame (positive = seat One,
    // [25] = seat One's bar, [0] = seat Two's bar) — stable orientation.
public sealed record PlayMove(int From, int To);            // actor's own numbering (notation frame)

// Live feed (GET /matches/{matchId}/live, text/event-stream) — one "type"-
// discriminated envelope union; payloads reuse the replay/summary contracts.
// No event ids / Last-Event-ID resume in v1 (see gaps): reconnect re-subscribes.
public abstract record LiveMatchEvent;                      // "type"-discriminated:
public sealed record LiveSnapshotEvent(int GameNumber, int SeatOneScore,        // "snapshot"
    int SeatTwoScore, IReadOnlyList<GameEntry> Entries) : LiveMatchEvent;       //   join-in-progress
public sealed record LiveGameStartedEvent(int GameNumber,                       // "gameStarted"
    int SeatOneScore, int SeatTwoScore) : LiveMatchEvent;                       //   number + score, no board yet
public sealed record LiveEntryEvent(GameEntry Entry) : LiveMatchEvent;         // "entry" (per-move increment)
public sealed record LiveGameEndedEvent(GameReplay Game) : LiveMatchEvent;     // "gameEnded" (canonical game)
public sealed record LiveTerminalEvent(MatchSummary Match) : LiveMatchEvent;   // "terminal" (final record, then close)

// BgTournament.Server — an application, not a library: all types internal.
// Its API is the wire protocol (PROTOCOL.md) plus the admin HTTP surface
// (shapes: BgTournament.Api; errors carry ErrorResponse):
//   GET  /engines                          connected engines (name, version, author, inMatch)
//   POST /matches                          {engineOne, engineTwo, matchLength, seed?, maxGames?}
//   GET  /matches                          every match record, creation order
//   GET  /matches/{matchId}                record summary (status, winner, scores, forfeit info)
//   GET  /matches/{matchId}/games          terminal match's replay, partial if interrupted (409 while running)
//   GET  /matches/{matchId}/live           text/event-stream: snapshot → per-move → terminal
//   POST /tournaments                      {participants[], matchLength, matchesPerPairing, seed?}
//   GET  /tournaments                      every tournament record, creation order
//   GET  /tournaments/{tournamentId}       status, standings, winner, per-match ledger (ids
//                                          resolve on GET /matches/{matchId})
// Config: Tournament:DecisionTimeoutSeconds (default 30), Tournament:HandshakeTimeoutSeconds (10).
```

## Pitfalls

- **Serialize only through `WireProtocol`.** `JsonSerializer.Serialize` on a
  concrete message type omits the `"type"` discriminator — the frame becomes
  undispatchable. The helper's base-typed parameter makes this structurally
  hard to hit; don't add parallel serialization paths.
- **`ToUnresolvedPlay` output must never be applied to a board.** It carries
  no hit encoding, and candidate matching is hit-insensitive — so it can
  *validate* as legal yet *apply* without sending the blot to the bar.
  `PlayResolver` is the sole sanctioned consumer; the canonical candidate is
  what gets applied and recorded. (The same validates-vs-applies asymmetry
  exists in `MoveGenerator.ApplyPlay` itself — flagged umbrella-side.)
- **No flips in `WireMapping`, ever.** The substrate's `OpponentView` is the
  system's only re-expression; a flip in the mapping double-applies the frame
  rule. The pin: `ToWireState_OfOpponentView_IsTheResponderFrame_NoDoubleFlip`.
- **A wire-text change is a protocol change.** The golden tests and
  `ProtocolDocTests` pin every byte on purpose. If a refactor (or a substrate
  rename reached through `WireMapping`) fails them, that is the alarm working:
  either fix the regression, or consciously version the protocol and move
  doc + goldens together.
- **`GeneratePlays` expresses "no legal move" as one empty candidate**, not an
  empty list. The empty wire play (`moves: []`) resolves against it by the
  same key match as any play — don't add a special case.
- **One in-flight query per engine is an invariant, not a hope.**
  `EngineConnection.QueryAsync` throws `InvalidOperationException` on overlap
  (a server bug); it holds because an engine is in at most one match (the
  registry busy flag) and `MatchRunner` is sequential.
- **Forfeit `matchEnded` reports 0–0** — partial scores are lost when the
  runner throws (no observer hook yet; umbrella-tracked). Aborted/Faulted
  matches end silently: v1 wire has no vocabulary for them (PROTOCOL.md §10).
- **In wire tests, await the client connect in the test body.** A
  fire-and-forget connect inside a background task swallows the real failure
  and surfaces as an unrelated registration timeout.
- **`EngineConnection.CloseAsync` is a half-close on purpose.** It sends the
  close frame (`CloseOutputAsync`) without waiting for the peer's ack — a
  misbehaving engine may never send one, and `WebSocket.CloseAsync` would hang
  the match run's `finally` forever (latent in Arc 2 when nothing awaited the
  run; the tournament loop awaits it). Don't "upgrade" it back to a full
  handshake.
- **A send failure is a disconnect, by translation.** `EngineConnection.SendAsync`
  converts transport-level send errors into `EngineDisconnectedException` so a
  closing-but-not-yet-observed connection forfeits cleanly instead of faulting
  the match. The tournament loop's connected pre-check is best-effort on top of
  this, not load-bearing.
- **`Tournament` (Core) is not thread-safe.** The server serializes every
  aggregate read and mutation through its `TournamentRecord.Gate`; keep any new
  access inside that lock.
- **The seed derivation is a reproducibility contract.** Changing
  `Tournament.DeriveMatchSeed` silently changes every tournament's dice.
  Recorded matches stay re-rollable (each match records its own seed), but a
  re-run of a past tournament seed would no longer reproduce it.
- **A tournament binds the sessions present at start.** A participant that
  disconnects — or is kicked by a forfeit close — forfeits its remaining
  matches without play, even if it reconnects under the same name. Both sides
  gone halts the tournament as Faulted (a match can be forfeited to no one).
- **Attribution is the substrate's, read not re-derived.** `ReplayProjection`
  reads each entry's stamped `OnRollSeat` and the subtype-owned attribution
  (`CubeTranscriptEntry.ActingSeat`, `GameEndedTranscriptEntry.Winner`) — it
  runs no sequencing heuristic. Don't reintroduce one (opening-die winner,
  flip-per-play, cube-no-flip): those rules now live on the entry types, and a
  local copy would be a second source of truth. The flip normalization must
  stay a `GameState.OpponentView` round-trip: a local mirror would be the
  system's second re-expression. `ProjectEntry`/`ProjectGame` are the single
  projection SSOT — the live feed reuses them, so a change is felt on both
  surfaces at once.
- **The live-feed observer adapter must stay non-throwing and non-blocking.**
  `LiveMatch`'s `IMatchObserver` callbacks run synchronously on the match loop,
  and the substrate kills the run if one throws. Keep every callback to fast
  in-memory work under the short lock plus non-blocking channel writes — never
  await, do I/O, or take a contended lock in a callback, and never let one
  throw uncontained (the `SafelyObserve` wrapper logs and faults the feed
  loudly instead). The terminal event is emitted by `MatchService`
  (`MarkTerminal`, after the record is folded), never from the observer — an
  aborted run gets no substrate callback at all.
- **The admin JSON is pinned byte-for-byte, wire-style.** `ApiGoldenTests`
  is the alarm: a shape or enum-string drift in `BgTournament.Api` is a
  contract change for BgArena_Blazor. Note the Web-default encoder escapes
  apostrophes (`'` → `\u0027`) in detail strings — pinned deliberately.

## Subproject-internal next steps

- **Live-feed resume (`Last-Event-ID`)** — a deliberate v1 gap. The SSE feed
  sends no event ids and honors no `Last-Event-ID`: a dropped connection is
  recovered by re-subscribing, whose fresh join-in-progress snapshot
  re-establishes state, making replay-by-id unnecessary and its buffering
  unjustified for now. Revisit if a consumer needs gapless reconnection.
- **Bounded live-feed backpressure** — subscriber channels are unbounded
  (server-to-server, one UI host, low event volume). If a stalled subscriber's
  backlog ever matters, bound the channel with a drop policy (and `log()` the
  drop) rather than letting it grow.
- **`xgid` decoration** — the state schema reserves it; populate when an
  in-tree XGID formatter exists (none today).
- **Runnable reference-bot executable** — a small console host over
  `EngineClient` + the reference bot, for connecting to a remote server the
  way third parties will (the SDK and bot are library-only today).
- **Unit-level pin for the late-reply discard** — the benign race is
  integration-shaped today; a focused `EngineConnection` test over an in-proc
  socket pair would pin the discard-once semantics directly.
- **Per-match timeout override** — `POST /matches` could carry an optional
  decision timeout; global-only configuration today.
- **Tournament rejoin policy** — v1 binds the sessions present at start;
  re-resolving a participant by name at each scheduled match (so a
  reconnected engine plays on) is a candidate once real remote engines flake.
- **Record timestamps** — match/tournament records carry creation order
  (internal `Sequence`) but no wall-clock times; a dashboard "when" column
  wants `startedAtUtc`/`endedAtUtc` on the summaries.
- **Disconnect-during-`matchStarted` forfeit gap** — an engine that vanishes
  in the narrow window while `matchStarted` is being sent can surface as a
  Faulted match rather than a Forfeited one (seen once as test flake; the
  send failure appears to bypass the disconnect translation). Deterministic
  repro + taxonomy fix wanted; execution semantics were out of scope for the
  read-surface arc.
