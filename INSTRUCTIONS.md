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
│   ├── VerifiableDice.cs                 fair-dice wire SSOT: algorithm id + commit-context format
│   ├── WireTimeControl.cs              time-control announcement shape (rides matchStarted)
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
│   ├── TimeControl.cs                  validated Fischer control + its JsonException-funnel converter
│   ├── EngineSummary.cs / MatchSummary.cs
│   ├── StandingEntry.cs / TournamentMatchEntry.cs / TournamentSummary.cs
│   ├── Seat.cs / CubeOwner.cs          seat-keyed identities for replay shapes
│   ├── GameResultKind.cs / CubeResponseAction.cs
│   ├── GamePosition.cs / PlayMove.cs   seat-One-frame position; mover-relative move
│   ├── GameEntry.cs                    "type"-discriminated union: play/cubeOffer/cubeResponse
│   ├── GameReplay.cs / MatchGamesResponse.cs   per-game replay + the endpoint envelope
│   ├── LiveMatchEvent.cs               live-feed envelope union: snapshot/gameStarted/entry/gameEnded/terminal
│   ├── ForfeitCause.cs / DecisionKind.cs       audit vocabularies (structured cause; timed-decision kind)
│   ├── AuditEvent.cs                   audit-timeline union (the fourth event family; no boards/moves)
│   └── MatchAuditResponse.cs           the audit endpoint envelope (status + integrity + events)
├── BgTournament.Core/                  execution-blind tournament domain (zero dependencies)
│   ├── TournamentFormat.cs             round-robin config: matchLength × matchesPerPairing
│   ├── ScheduledMatch.cs               one schedule row: seats + the derived dice seed
│   ├── StandingsRow.cs                 one standings line: wins, losses, Sonneborn-Berger
│   └── Tournament.cs                   the aggregate: schedule, results, tie-break ladder
├── BgTournament.Server/                the tournament host (all types internal)
│   ├── Program.cs                      /engine (WS) + the admin HTTP endpoints; rehydrate-before-serve
│   ├── EngineSocketEndpoint.cs         handshake gate; named rejections
│   ├── EngineConnection.cs             receive loop; one in-flight query; Closed task
│   ├── IEngineChannel.cs               query seam (EngineConnection live, faked in tests)
│   ├── RemoteEngineAgent.cs            IPlayAgent+ICubeAgent over the channel; taxonomy
│   ├── PlayResolver.cs                 wire play → canonical hit-encoded candidate
│   ├── CountingDiceSource.cs           IDiceSource wrapper counting rolls (fair-mode play-query roll index)
│   ├── MatchClock.cs                   per-match Fischer clock over the TimeProvider seam; settlement reports
│   ├── DecisionKind.cs                 the server's one decision vocabulary (clock evidence + query labels)
│   ├── EngineRegistry.cs               sessions by name; per-engine busy flag
│   ├── MatchService.cs                 match host: attribution, forfeits, records, journal-settled gate
│   ├── TournamentService.cs            tournament host: claims, orchestration, folding
│   ├── EngineFailureExceptions.cs      timeout / disconnected / protocol-violation
│   ├── ForfeitCause.cs                 the structured forfeit taxonomy the journal records
│   ├── TournamentOptions.cs            decision + handshake timeouts (appsettings)
│   ├── ApiMapping.cs                   the ONLY server-internal → Api projections
│   ├── ReplayProjection.cs             transcript → replay contract: stamped-seat read + flip
│   ├── MatExportProjection.cs          record → MatExporter factory choice (.MAT surface)
│   ├── AuditProjection.cs              journal → audit contract: the arbitration timeline walk
│   ├── LiveMatch.cs                    per-match live cache + SSE broadcast (the IMatchObserver adapter)
│   ├── CompositeMatchObserver.cs       fans the runner's callbacks to LiveMatch + MatchJournal
│   └── Persistence/                    the durable records journal (Arc 9) + arbitration log (Arc 10)
│       ├── MatchJournalEvent.cs        match-journal DTO union ("type"-discriminated JSONL lines)
│       ├── TournamentJournalEvent.cs   tournament-journal DTO union
│       ├── ServerJournalEvent.cs       server-journal DTO union (engine lifecycle evidence)
│       ├── JournalShapes.cs            journal-local enums/shapes (never Api or substrate enums)
│       ├── JournalCodec.cs             schema versions + the ONLY journal (de)serialization path
│       ├── JournalMapping.cs           the ONLY substrate/server ↔ journal correspondences (both ways)
│       ├── IJournalStore.cs            the store seam: raw sinks/sources by kind + id
│       ├── FileJournalStore.cs         <DataDirectory>/matches|tournaments|server/<id>.jsonl
│       ├── JournalWriter.cs            per-journal channel + background pump; flush per event
│       ├── JournalReader.cs            the one read policy: torn-tail / corruption trusted-prefix parse
│       ├── MatchJournal.cs             the write-through IMatchObserver sibling of LiveMatch
│       ├── TournamentJournal.cs        created / matchStarted / result / terminal
│       ├── ServerJournal.cs            hosted per-boot segment: started/connect/disconnect/reject/stopped
│       ├── JournalRehydrator.cs        startup fold: journals → records, before endpoints serve
│       └── PersistenceOptions.cs       Persistence:DataDirectory (appsettings)
├── BgTournament.EngineClient/          .NET SDK + reference bot
│   ├── EngineClient.cs                 connect/handshake/serve loop over local agents; optional fair-dice verify hook
│   ├── EngineIdentity.cs               hello identity
│   ├── HandshakeRejectedException.cs
│   ├── DiceVerification.cs             pure fair-dice verifier + DiceVerificationReport / ObservedRoll
│   ├── DiceAuditRecorder.cs            per-match observation accumulator feeding the verifier at match end
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
    ├── WireMatchSmokeTests.cs          full wire matches: random pair + BgInference + fair-mode (SmokeC)
    ├── DiceVerificationTests.cs        the pure fair-dice verifier (verify / mismatch / unknown-algo / bounds)
    ├── FairDiceLifecycleTests.cs       wire-boundary branch: seeded omits fields, fair publishes commitment
    ├── TournamentFairDiceTests.cs      unseeded tournament ⇒ per-match keys; seeded ⇒ none
    ├── TimeControlTests.cs             the validated value type + its JsonException funnel
    ├── MatchClockTests.cs              Fischer arithmetic, deterministic on a fake TimeProvider
    ├── TimeControlWireTests.cs         clocks end to end: announcement, pools, flag fall, tournament fold
    ├── ListEndpointTests.cs            /matches + /tournaments listings, creation order
    ├── ReplayProjectionTests.cs        the stamped-seat projection on scripted-dice MatchRunner runs
    ├── ReplayEndpointTests.cs          /matches/{id}/games over TestServer, 404/409/partial
    ├── LiveMatchTests.cs               the live cache/broadcast core over its IMatchObserver surface
    ├── LiveEndpointTests.cs            /matches/{id}/live SSE end to end: ordering, forfeit, already-done
    ├── MatExportEndpointTests.cs       /matches/{id}/export.mat: golden, money, forfeit/aborted/faulted, 404/409
    ├── TournamentCoreTests.cs          domain pins: schedule, seeds, tie-break ladder
    ├── TournamentServerTests.cs        tournament claims, validation, forfeit folding
    ├── TournamentSmokeTests.cs         3-engine round-robin over the wire to a winner
    ├── JournalGoldenTests.cs           byte-for-byte journal-event pins, every event + enum (all three kinds)
    ├── JournalMappingTests.cs          substrate ↔ journal fidelity round trips (hits, frames, taxonomy)
    ├── RehydrationTests.cs             restart identity, torn tail, corruption, fair-dice evidence,
    │                                   v1 tolerance, evidence-ignored fold, unknown-version skip
    ├── AuditEndpointTests.cs           /matches/{id}/audit: replay join, clock trail, fair packet,
    │                                   deterministic drain gate, damage surfaces, late-reply hook pin
    └── ServerJournalTests.cs           segment per boot; connect/disconnect/reject/stopped evidence
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
ctors); timeout/flag fall/disconnect → engine-name-carrying server exceptions; external
cancellation passes untranslated. `MatchService` owns what the substrate
deliberately does not: seat↔engine attribution, the v1 forfeit policy
(forfeit the match), the proactive disconnect watch (first connection to end
mid-match takes the forfeit — even while its opponent is thinking), per-receiver
`matchStarted`/`matchEnded` framing, and in-memory record retention (full
`MatchResult` with transcripts for completed matches). Aborted/Faulted matches
end silently on the wire — v1 has no vocabulary for them and never lies about
a reason.

**Provably-fair dice (commit-and-reveal).** Unseeded matches run on
verifiable dice (BgGame_Lib's `VerifiableDiceSource`): the server generates a
per-match `DiceKey`, publishes `key.Commit("bg-tournament:match:<matchId>")`
on `matchStarted` (with the `hmac-sha256-dice-v1` algorithm id) before the
first roll, and reveals the key on `matchEnded`, so either player re-derives
every roll and confirms the dice were fixed in advance and never adapted.
**Fair mode is the default** — omitting the `POST /matches` seed selects it;
an explicit seed keeps the reproducible, uncommitted `SeededDiceSource`
(dev/repro), which sends none of the fair fields. All additions are
**additive under PROTOCOL.md §2's ignore-unknown-fields rule — the protocol
stays v1** (an unaware engine ignores them and plays identically). The
algorithm id and context format are single-sourced in `Protocol/VerifiableDice`
so producer, verifier, and goldens speak one string. Each `playQuery` also
carries a `rollIndex` (fair mode only) — the roll's 0-based stream position,
sourced from a `CountingDiceSource` wrapping the real source — so an engine
places each observed roll (a gapped subsequence: it never sees opponent turns,
dances, or opening re-rolls) at its true position. Verification is split
SSOT-style: the server produces; a **pure** `DiceVerification.VerifyMatchDice`
(in EngineClient, callable by any C# client) checks commitment + each observed
roll and returns an immutable report; `EngineClient`'s optional
`onDiceVerified` hook auto-records observations and delivers the report,
never throwing on failure (the cheating-server policy is the consumer's).
**What it proves:** non-adaptivity (the whole stream is fixed before roll one)
and observed-roll integrity. **What it doesn't:** non-selection — server-only
entropy can't rule out a cherry-picked committed sequence; contributory nonces
(engine participation → protocol v2) are the recorded upgrade path. Rolls no
engine saw are auditable from the retained transcript, not the wire. Unseeded
**tournaments** run each scheduled match on its own generated key; the
scheduled seed stays structural (schedule shape) in every mode and is not the
fair-mode dice driver. Seeded tournaments keep the SplitMix64 derivation.

**Time controls (Fischer clocks).** `POST /matches`/`/tournaments` may carry
a `timeControl` — `BgTournament.Api.TimeControl`, a constructor-validated
immutable value (initial pool + per-decision increment, in seconds; TimeSpan
views; a dedicated JSON converter folds constructor rejections into
`JsonException`, so an invalid control hits the host's standard 400 funnel
like any other malformed field instead of a binding 500). With one, the match
runs on a per-match `MatchClock`: per-seat pools debited by the wall time the
server measures around each decision query and credited the increment per
answered decision — latency deliberately on the player's clock (the only
server-authoritative measurement; PROTOCOL.md §10 says so honestly). The
control **replaces** the flat `DecisionTimeoutSeconds` guard for that match:
the remaining pool is the only per-decision limit, enforced by a flag
`CancellationTokenSource` armed with the pool, so an emptied pool cancels the
in-flight query and surfaces as `EngineFlagFallException` — the fourth
forfeit cause, folded by `MatchService.RecordForfeit` like the others. Clock
logic reads time exclusively through the DI `TimeProvider` (never ambient),
which makes every clock test deterministic and pre-stages the timestamp
source a future arbitration log will reuse. On the wire it is all additive v1
(the fair-dice precedent): `matchStarted.timeControl` announces the control
so engines budget the match, and every query carries both pools
(`yourTimeRemainingSeconds`/`opponentTimeRemainingSeconds`, stamped at query
issuance, frame-rule naming). Wire-only in v1: substrate agents and
EngineClient-hosted agents never see the clock (SDK surfacing is a recorded
next step). A tournament-level control governs every scheduled match, each on
a fresh clock; a flag fall folds as an ordinary win for the non-offender.

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
(`LiveMatchEvent`). A game's frame-free start context (seat-absolute entering
scores + the Crawford flag) rides `OnGameStarted`'s `GameStartContext` straight
from the substrate onto `gameStarted` and the current-game `snapshot`; the feed
holds those values verbatim rather than re-folding a score tally of its own, so
a live Crawford game renders as Crawford (it previously did not). The one terminal event is emitted by the host
(`MatchService`, after the outcome is folded into the record), not the
substrate — which emits nothing on abort — so it fires once for every outcome
(completed / forfeited / aborted / faulted) and carries the truth. The
collected completed games are also the retained replay source, so a
forfeited/faulted match keeps the games that finished before the break.
Spectating is invisible on the wire: PROTOCOL.md stays at v1, engines see
nothing.

**Export shape.** `GET /matches/{matchId}/export.mat` serves a terminal match as
Jellyfish `.MAT` text — a `text/plain` attachment (`match_{id}.mat`). The bytes
come straight from BgMatchFormat_Lib (`MatExporter.Export`, LF-only, no trailing
whitespace) and are served verbatim; BgTournament.Api stays out of it (this is
text, not a JSON contract). `MatExportProjection` is the counterpart of
`ReplayProjection`: it maps the record onto the exporter's factory choice —
Completed length > 0 → `ForMatch`, Completed length 0 → `ForMoneySession`,
Forfeited → `ForForfeit` (the non-forfeiting seat wins, read off the same
`ForfeitedBy` taxonomy), Aborted/Faulted → `ForAbandoned` (winner-less, with a
one-line reason from the status). Engine names map to Player 1/2 on the same
absolute seat convention as replay, and `matchId` rides a `Match ID` header tag.
The completed games are the retained replay source; a forfeited/aborted/faulted
match also carries its trailing in-flight game, which `LiveMatch` now retains as
the untouched substrate `Transcript` (the replay projections stay seat-One-frame
API shapes; the export needs the raw substrate frame). Same terminal-only gate
as replay: a running match answers 409 pointed at `/live`.

**Durable records (the journal).** Records survive restarts via an
append-only, schema-versioned JSONL journal per match and per tournament under
`Persistence:DataDirectory` — designed as an *ordered audit record* (the Arc 10
arbitration log reads this vocabulary), not a cache-warming format. Every event
carries an `at` timestamp from the DI `TimeProvider`; the first line is always
the `created` header (identity, configuration, `schemaVersion`, and — fair
mode — the *escrowed* `DiceKey`: it must be on disk before terminal or an
interrupted match could never reveal it; the commitment stays derived, the
no-drift rule). Match events mirror the observer flow at full substrate
fidelity — `started`, `gameStarted`, `play`/`cube`/`gameEnded` transcript
entries in their native frames with hit encoding intact (unlike the wire) — and
a host-written `terminal` (status, winner, scores, structured `ForfeitCause`)
closes the file. The tournament journal is thin: `created`,
`matchStarted` (the schedule-index ↔ match-id linkage), one `result` per fold,
`terminal`; the schedule itself is re-derived, never stored. The write path is
`MatchJournal`, a sibling `IMatchObserver` beside `LiveMatch` under a
`CompositeMatchObserver` — same discipline (fast in-memory mapping, non-blocking
channel write, never awaits/throws uncontained; a journal fault logs loudly and
stops journaling while the match plays on) — draining through a per-journal
background `JournalWriter` that flushes per event (the settled durability tier:
survives process death; power loss only costs the torn tail the fold already
tolerates). The layering is strict SSOT: journal DTOs are their own family with
journal-local enums (`JournalCodec` the only (de)serialization path,
`JournalMapping` the only correspondences, `JournalGoldenTests` the byte pins),
and `IJournalStore`/`FileJournalStore` move raw streams only. At startup
`JournalRehydrator` folds every journal back into records *before endpoints
serve*, replaying entries through a fresh `LiveMatch`'s own callbacks (no
second fold path) and rebuilding `GameRecord`s from the stamped facts — so
replay, `.MAT` export, and summaries serve identically across a restart.
Damage policy: a torn final line is dropped with a warning; corruption on an
earlier line folds the trusted prefix with a corruption-naming detail; a
journal with no trusted `terminal` event folds to `MatchStatus.Interrupted` /
`TournamentStatus.Interrupted` — terminal, evidence intact, `endedAtUtc`
honestly null. Restart listing order is header-timestamp order with an
ordinal-id tiebreak. Tournament resume-after-restart is deliberately not
implemented (the journaled results enable it later); an interrupted
tournament's finished matches remain fully served records.

**Arbitration log (the journal as evidence, and its read surface).** Arc 10
extends the Arc 9 journal into the arbitration record. Match journals (schema
v2; v1 files fold forever) gain two evidence-only event types the fold
ignores: `clock` — one per settled clocked decision, written from
`MatchClock`'s settlement callback (seat, decision kind, measured think,
increment credited, pool before/after; the clock stays the one measurement
SSOT, flat-regime matches measure nothing) — and `lateReply`, raised by
`EngineConnection.LateReplyDiscarded` when the benign §8 race discards a
reply to an abandoned query (proof the engine answered, too late to count).
Ordering is part of the vocabulary: a clock event precedes the entry it
timed; a `cubeOffer` clock event with no cube entry was a declined double
window (think time replay elides); a trailing clock event is the decision
that ended the match; a dance is a play entry with no clock event (never
queried). A new **server journal** (`server/<id>.jsonl`, one segment per
boot, independently versioned) records engine lifecycle — `started` header,
`engineConnected`/`engineDisconnected`, `handshakeRejected` with the exact
wire reason (one funnel in `EngineSocketEndpoint`), and a graceful `stopped`
marker whose absence is crash evidence. It is deliberately **evidence-only**:
sessions are ephemeral, nothing rehydrates from it. The read surface is
`GET /matches/{matchId}/audit` — new `BgTournament.Api` audit DTOs (the
fourth event family) projected by `AuditProjection` over the journal's
trusted prefix (`JournalReader`, the parse-policy SSOT shared with
rehydration) at **arbitration altitude**: timestamps, attribution, causes,
clock arithmetic; no boards or moves — game number + entry index join to
replay. The structured `ForfeitCause` rides the terminal audit event, which
is **record-derived** (skipping the journal's terminal line): identical
content for a folded terminal, and the only truthful close for Interrupted —
`at` honestly null, the escrowed fair-dice key revealed. Fair mode makes the
response a self-contained verification packet: `created` carries the derived
commitment (never stored — `MatchRecord.Commitment`, the wire's own
derivation) + algorithm, play events carry the rolls, `terminal` reveals the
key. Refusals mirror replay (404 unknown; 409-to-`/live` while Running), and
the endpoint awaits `MatchRecord.JournalSettled` — completed unconditionally
on every finalize path — so a read never races the journal drain. Wire
untouched: PROTOCOL.md stays v1; engines see none of this.

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

// Fair-dice additive fields (fair mode only; omitted for explicit-seed matches):
//   MatchStartedMessage.DiceCommitment?, .DiceAlgorithm?   (published before roll one)
//   MatchEndedMessage.DiceKey?                             (revealed at match end)
//   PlayQueryMessage.RollIndex?                            (this roll's 0-based stream position)
// Time-control additive fields (clocked matches only; omitted in the flat regime):
//   MatchStartedMessage.TimeControl?                       (WireTimeControl, announced up front)
//   QueryMessage.YourTimeRemainingSeconds?, .OpponentTimeRemainingSeconds?
//                                                          (both pools, as of query issuance)
public sealed record WireTimeControl { public required double InitialSeconds; public required double IncrementSeconds; }
public static class VerifiableDice          // fair-dice wire SSOT
{
    public const string AlgorithmId = "hmac-sha256-dice-v1";
    public static string ContextFor(string matchId);        // "bg-tournament:match:{matchId}"
}

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
                        ILogger<EngineClient>? logger = null,
                        Action<DiceVerificationReport>? onDiceVerified = null);  // fair-dice hook (opt-in)
    public Task RunAsync(Uri serverUri, CancellationToken ct = default);
    public Task ServeAsync(WebSocket socket, CancellationToken ct = default);
}

// Fair-dice verification — pure, standalone; also driven automatically by the
// EngineClient hook above (never throws on a failed verdict, it reports it).
public enum DiceVerificationOutcome { Verified, CommitmentMismatch, RollMismatch, UnknownAlgorithm }
public sealed record ObservedRoll(int Index, int Die1, int Die2);
public sealed record DiceVerificationReport(DiceVerificationOutcome Outcome, int ObservedRollCount, string? Detail)
{ public bool Verified { get; } }
public static class DiceVerification
{
    public static DiceVerificationReport VerifyMatchDice(
        string matchId, DiceCommitment commitment, string algorithm,
        DiceKey revealedKey, IReadOnlyList<ObservedRoll> observedRolls);
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
public sealed record StartMatchRequest(...);               // POST /matches (optional TimeControl)
public sealed record StartTournamentRequest(...);          // POST /tournaments (optional TimeControl)
public sealed record TimeControl                            // validated Fischer control; null = flat regime
{
    public TimeControl(double initialSeconds, double incrementSeconds);  // finite; >0 / ≥0; TimeSpan-representable
    public double InitialSeconds { get; }                   // wire "initialSeconds"
    public double IncrementSeconds { get; }                 // wire "incrementSeconds"
    public TimeSpan Initial { get; }                        // [JsonIgnore] TimeSpan views
    public TimeSpan Increment { get; }
}   // JSON binding via its own converter: invalid values → JsonException (the standard 400 funnel)
public sealed record EngineSummary(...);                   // GET /engines rows
public sealed record MatchSummary(...);                    // match endpoints' projection; carries
                                                           //   timeControl + startedAtUtc/endedAtUtc
                                                           //   (endedAtUtc null while running AND on
                                                           //   Interrupted — the end died with the server)
public sealed record TournamentSummary(...);               // tournament endpoints' projection; same
                                                           //   timeControl + timestamp fields
public sealed record StandingEntry(...);                   //   one standings line
public sealed record TournamentMatchEntry(...);            //   one schedule-ledger row
public enum MatchStatus { Running, Completed, Forfeited, Aborted, Faulted, Interrupted }
public enum TournamentStatus { Running, Completed, Aborted, Faulted, Interrupted }
    // Interrupted ("interrupted"): terminal; produced only by journal rehydration
    // (the server died under the record). A live match/tournament never carries it.

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
    int SeatTwoScore, bool IsCrawford, IReadOnlyList<GameEntry> Entries)        //   join-in-progress:
    : LiveMatchEvent;                                                           //   current game's context
public sealed record LiveGameStartedEvent(int GameNumber,                       // "gameStarted"
    int SeatOneScore, int SeatTwoScore, bool IsCrawford) : LiveMatchEvent;      //   frame-free ctx, no board yet
public sealed record LiveEntryEvent(GameEntry Entry) : LiveMatchEvent;         // "entry" (per-move increment)
public sealed record LiveGameEndedEvent(GameReplay Game) : LiveMatchEvent;     // "gameEnded" (canonical game)
public sealed record LiveTerminalEvent(MatchSummary Match) : LiveMatchEvent;   // "terminal" (final record, then close)

// Audit (GET /matches/{matchId}/audit) — the arbitration timeline: the fourth
// event family (wire / journal / replay / audit), at arbitration altitude —
// no boards or moves; join to replay by GameNumber + EntryIndex.
public enum ForfeitCause { ContractViolation, Timeout, FlagFall, Disconnect, NeverConnected }
public enum DecisionKind { Play, CubeOffer, CubeResponse }
public sealed record MatchAuditResponse(                     // the endpoint envelope
    string MatchId, string EngineOne, string EngineTwo,
    MatchStatus Status,                                      // the record's truth (terminal-only)
    string? Integrity,                                       // corruption note; null when whole
    IReadOnlyList<AuditEvent> Events);                       // last event is always "terminal"
public abstract record AuditEvent(DateTimeOffset? At);      // "type"-discriminated; At null only on
                                                            // an Interrupted match's terminal event:
public sealed record AuditCreatedEvent(..., string? DiceAlgorithm,          // "created" — fair mode:
    string? DiceCommitment, TimeControl? TimeControl);                      //   derived commitment (never stored)
public sealed record AuditStartedEvent(...);                                // "started"
public sealed record AuditGameStartedEvent(...);                            // "gameStarted"
public sealed record AuditPlayEvent(..., Seat Actor, int Die1, int Die2);   // "play" (roll evidence)
public sealed record AuditCubeOfferEvent(..., Seat Actor);                  // "cubeOffer"
public sealed record AuditCubeResponseEvent(..., CubeResponseAction Action);// "cubeResponse"
public sealed record AuditGameEndedEvent(..., Seat Winner, ...);            // "gameEnded"
public sealed record AuditClockEvent(..., Seat Seat, DecisionKind Decision, // "clock" — per settled
    double ThinkSeconds, bool IncrementCredited,                            //   clocked decision;
    double RemainingBeforeSeconds, double RemainingAfterSeconds);           //   precedes its entry
public sealed record AuditLateReplyEvent(..., Seat Seat, string RequestId); // "lateReply" (the §8 race)
public sealed record AuditTerminalEvent(..., MatchStatus Status,            // "terminal" — record-derived;
    ForfeitCause? ForfeitCause, ..., string? DiceKey);                      //   fair mode reveals the key

// BgTournament.Server — an application, not a library: all types internal.
// Its API is the wire protocol (PROTOCOL.md) plus the admin HTTP surface
// (shapes: BgTournament.Api; errors carry ErrorResponse):
//   GET  /engines                          connected engines (name, version, author, inMatch)
//   POST /matches                          {engineOne, engineTwo, matchLength, seed?, maxGames?, timeControl?}
//   GET  /matches                          every match record, creation order
//   GET  /matches/{matchId}                record summary (status, winner, scores, forfeit info)
//   GET  /matches/{matchId}/games          terminal match's replay, partial if interrupted (409 while running)
//   GET  /matches/{matchId}/live           text/event-stream: snapshot → per-move → terminal
//   GET  /matches/{matchId}/export.mat     terminal match as Jellyfish .MAT text (attachment; 409 while running)
//   GET  /matches/{matchId}/audit          terminal match's arbitration timeline (409 while running)
//   POST /tournaments                      {participants[], matchLength, matchesPerPairing, seed?, timeControl?}
//   GET  /tournaments                      every tournament record, creation order
//   GET  /tournaments/{tournamentId}       status, standings, winner, per-match ledger (ids
//                                          resolve on GET /matches/{matchId})
// Config: Tournament:DecisionTimeoutSeconds (default 30; flat regime only — a
// timeControl replaces it), Tournament:HandshakeTimeoutSeconds (10),
// Persistence:DataDirectory (default "data" under the content root; the durable
// journals live in matches/, tournaments/, and server/ beneath it, and fair-mode
// dice keys are escrowed there — the directory shares the server's trust domain).
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
- **The seed derivation is a reproducibility contract — and now a
  durable-format contract.** Changing `Tournament.DeriveMatchSeed` (or the
  schedule construction) silently changes every tournament's dice, and it also
  changes what rehydration rebuilds: the tournament journal deliberately stores
  only participants + format + seed and *re-derives* the schedule, so a
  derivation change re-folds every stored tournament differently even though no
  JSON byte moved. Treat such a change as schema-relevant: it needs the same
  conscious versioning/migration thought as a journal shape change.
- **Omitting the seed now means fair mode, not a random seeded match.** `POST
  /matches`/`/tournaments` with no seed selects verifiable dice (commitment on
  `matchStarted`, reveal on `matchEnded`); only an explicit seed keeps
  `SeededDiceSource`. Any test that wants deterministic dice must pass a seed.
- **`rollIndex` leans on the runner's roll-then-query sequencing.**
  `CountingDiceSource` counts `Roll()` calls; `RemoteEngineAgent` stamps
  `rollIndex = RollsProduced - 1`, correct only because `MatchRunner` takes
  exactly one roll immediately before each play query. Cube offers/responses
  take no roll; a dance takes one but sends no query; opening ties each take
  one. The fair-mode smoke re-derives the whole stream against the transcript,
  so a sequencing regression fails loudly. Don't stamp the index anywhere but at
  play-query time.
- **The fair-mode `Seed` is structural, not dead code.** In fair mode
  `MatchRecord.Seed` (and `TournamentMatchEntry.Seed`) is still recorded — the
  admin `MatchSummary.Seed` stays `int`, so the Api surface is unchanged — but
  the committed `DiceKey` drives and reproduces the dice, not the seed. The
  tournament seed always governs schedule *structure*; it drives dice only in
  seeded mode.
- **Fair-dice fields are additive v1.** New wire fields ride the ignore-unknown
  policy (PROTOCOL.md §2); the version stays 1. The algorithm id and commit
  context are the frozen public contract — `Protocol/VerifiableDice` is their one
  home; a change breaks every external verifier and the session-1 vectors.
  `DiceCommitment.Verifies` and the context string must match byte-for-byte
  across producer and verifier or verification silently fails.
- **A time control replaces the flat guard — don't reintroduce it.** Under a
  `MatchClock` the remaining pool is the only per-decision limit (PROTOCOL.md
  §10: a large pool must be spendable on one long think). `DecisionTimeoutSeconds`
  applies solely to clock-less matches; adding a second `CancelAfter` to the
  clocked bracket would silently change match outcomes.
- **No ambient time in clock logic.** `MatchClock` — and anything that joins
  it — reads time exclusively through the injected `TimeProvider`
  (`GetTimestamp`/`GetElapsedTime`, and the flag CTS's TimeProvider
  constructor). A `DateTime.UtcNow` or `Stopwatch` would break clock-test
  determinism and fork the timestamp source a future arbitration log reuses.
- **Only answered decisions earn the increment, settled exactly once.**
  `MatchClock.Decision` debits measured time on dispose (idempotent) and
  credits the increment only if `MarkAnswered()` ran first — a flag fall,
  violation, disconnect, or external cancellation debits without credit. Keep
  `MarkAnswered` after the exchange returns, never before.
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
- **The journal format is durable — a byte change orphans files already on
  disk.** `JournalGoldenTests` pins every event and enum string; a deliberate
  change bumps `JournalCodec.SchemaVersion` and decides the migration story
  consciously. Rehydration accepts the [`MinSchemaVersion`, `SchemaVersion`]
  range — old files fold forever — and skips unknown versions loudly. A **new
  event type is a schema change here**, not a wire-style additive field: the
  codec is deliberately strict, so an unknown discriminator under an old
  stamp reads as corruption to an old reader (that is why the Arc 10 evidence
  events came with the v1→v2 bump; the wire's additive-under-v1 precedent
  does not transfer). The server journal versions independently
  (`ServerSchemaVersion`) — the kinds are separate durable formats. Journal
  DTOs use journal-local enums, never the Api or substrate ones: an Api
  rename must never silently rewrite the durable format. Serialize only
  through `JournalCodec` (same discriminator footgun as `WireProtocol`), map
  only through `JournalMapping`, parse files only through `JournalReader`
  (the one torn-tail/corruption policy, shared by rehydration and audit).
- **Journal observer callbacks obey the live-feed rule.** `MatchJournal`'s
  `IMatchObserver` callbacks run synchronously on the match loop: fast
  in-memory mapping + a non-blocking channel write, never awaiting, doing I/O,
  or throwing uncontained. Disk work happens only on the `JournalWriter` pump;
  a journal failure logs loudly and stops journaling — the match must play on,
  and the record must never wait on disk.
- **Journal moves keep the hit signs the wire strips.** `JournalMapping`
  serializes `Play` moves with the raw sign-encoded destinations (negative =
  hit, 0 = bear-off) so rehydration rebuilds the exact substrate `Play`.
  Reusing `WireMapping.ToWireMoves` here would silently lose hits from every
  stored transcript — replay would still look legal (matching is
  hit-insensitive) while the audit record lied.
- **The damage policy is tail-lenient, prefix-strict.** Only a journal's
  *final* unparseable line is a torn tail (crash mid-append) and dropped with a
  warning; a failure on any earlier line is corruption — the fold stops there
  and serves the trusted prefix as Interrupted with a corruption-naming detail.
  Never trust events past a corrupt line, even a well-formed terminal.
- **Interrupted is rehydration's word alone.** No code path ever *writes* an
  interrupted terminal event — the status is precisely "the journal has no
  trusted terminal line". A shutdown race can therefore demote Aborted to
  Interrupted on the next boot (terminal written but not yet flushed when the
  process died): both are honest "server stopped it" statuses; don't "fix"
  this by writing terminal events early.
- **`Restore` runs before the endpoints serve, once.** Rehydrated records are
  terminal, `MarkTerminal`'d, and journal-less (`Journal` null — the file on
  disk already is their complete history). Nothing may rehydrate to Running,
  and nothing may append to a rehydrated journal.
- **Clock evidence events obey an ordering vocabulary — and only clocked
  matches have them.** A `clock` event is journaled at settlement, *before*
  the runner records the resulting transcript entry; audit readers rely on
  the three corollaries (declined double window = `cubeOffer` clock with no
  cube entry; trailing clock = the fatal decision; dance = play entry with no
  clock event, the engine was never queried). Don't journal the settlement
  anywhere but the `MatchClock` callback (the one measurement SSOT), and
  don't invent flat-regime timing — the flat regime measures nothing.
- **The server journal is evidence-only — never rehydrate it.** Sessions are
  ephemeral; the registry is live state alone. One segment per boot keeps the
  store's create-once contract; the `stopped` marker's *absence* is the crash
  evidence, so never write it anywhere but graceful shutdown.
- **The terminal audit event is record-derived; the audit read waits on
  `JournalSettled`.** `AuditProjection` skips the journal's terminal line and
  closes the timeline from the `MatchRecord` (identical for a folded
  terminal; the only truthful close for Interrupted, which also revealed the
  escrowed key there). Don't re-read the terminal from the file — and don't
  remove the endpoint's `await record.JournalSettled`: a match turns terminal
  before its journal drains, and without the gate an immediate read serves a
  short file. `MarkJournalSettled` completes **unconditionally** on every
  finalize path — a gate left pending turns a millisecond race into a hung
  request, strictly worse than the short read it prevents.
- **The derived dice commitment must stay derived.** The audit `created`
  event's commitment comes from `MatchRecord.Commitment` (the same
  `DiceKey.Commit` + context path `matchStarted` used) — never store it in
  the journal or derive it a second way; the no-drift pin is the wire↔audit
  byte equality in `AuditEndpointTests`.

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
- **Flat-regime decision timing** — the arbitration log's `clock` events
  exist only for clocked matches, because `MatchClock` is the one measurement
  seam. Giving flat-regime matches per-decision think-time evidence would
  need a regime-neutral measurement scope; a candidate if arbitration ever
  needs it there.
- **Per-match timeout override** — `POST /matches` could carry an optional
  decision timeout; global-only configuration today. (A Fischer `timeControl`
  already gives per-match timing; this item is about the flat regime.)
- **SDK clock surfacing** — time controls are wire-only in v1: EngineClient
  deserializes `matchStarted.timeControl` and the per-query pools but exposes
  none of it, and substrate `IPlayAgent`/`ICubeAgent` carry no time parameter,
  so hosted agents cannot budget. Surface the clock through the SDK (the
  umbrella menu pairs this with the competitor onboarding pack).
- **Tournament rejoin policy** — v1 binds the sessions present at start;
  re-resolving a participant by name at each scheduled match (so a
  reconnected engine plays on) is a candidate once real remote engines flake.
- **Journal retention policy** — journals accumulate forever (one small file
  per match/tournament, plus one server-journal segment per boot; the
  per-boot segmentation is the growth answer at file level, total growth
  stays unbounded). Nothing prunes or archives. Fine at hobby scale; revisit
  when the data directory's growth is ever noticed — server segments are the
  natural first pruning target (evidence-only, orderable by header
  timestamp).
- **Disconnect-during-`matchStarted` forfeit gap** — an engine that vanishes
  in the narrow window while `matchStarted` is being sent can surface as a
  Faulted match rather than a Forfeited one (seen once as test flake; the
  send failure appears to bypass the disconnect translation). Deterministic
  repro + taxonomy fix wanted; execution semantics were out of scope for the
  read-surface arc.
