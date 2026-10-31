# BgTournament

> Collaboration contract: [`../AGENTS.md`](../AGENTS.md)
> Umbrella status & dependency graph: [`../INSTRUCTIONS.md`](../INSTRUCTIONS.md)
> Mission & principles: [`../VISION.md`](../VISION.md)
> Open work: the [tracker](https://github.com/halheinrich/backgammon/issues), label `BgTournament`.

## What it is for

The computer-tournament arena: a versioned, language-neutral WebSocket+JSON
protocol that *is* the engine contract; an ASP.NET Core server that adapts each
connected engine onto `IPlayAgent`/`ICubeAgent`, so BgGame_Lib's `MatchRunner`
referees wire matches without knowing sockets exist, and hosts round-robin
tournaments over that same machinery; and a .NET client SDK that turns any
in-proc agent pair into a remote engine. Server-authoritative throughout: the
server owns dice, legality, state, pairing and refereeing, and always records
a seed; engines only answer decision queries.

**The one consumer.** BgArena_Blazor takes `BgTournament.Api` and the admin
HTTP surface. Its test project also references Server and EngineClient,
test-only, for its in-proc smoke; the Server grants it `InternalsVisibleTo`.

**The three root documents**, each with one job, are for engine authors:

- [`PROTOCOL.md`](PROTOCOL.md) — the wire SSOT: the canonical engine contract,
  language-neutral and versioned.
- [`ONBOARDING.md`](ONBOARDING.md) — the competitor on-ramp: register, connect,
  play.
- [`RULES.md`](RULES.md) — championship conduct: attestation, forfeits, fair
  dice.

`ONBOARDING.md` and `RULES.md` link into `PROTOCOL.md` for wire behavior and
reproduce no wire example, which keeps `ProtocolDocTests` the one pin on wire
text. This document is for whoever changes the code.

## Stack

C# / .NET 10: an ASP.NET Core server (Kestrel + WebSockets), four class
libraries, a console bot, and an xUnit test project
(`Microsoft.AspNetCore.Mvc.Testing` for in-process wire tests). Visual Studio
2026, Windows.

## Build and test

From this directory:

```bash
dotnet build BgTournament.slnx
```

```bash
dotnet test BgTournament.slnx
```

The solution has twelve projects: this repo's seven and, by relative path, the
five of Depends on, so it needs the umbrella checkout with those submodules
initialized.

The suite reads no `TestData`. It does need the BgRLEngine sibling, though the
build does not: `TournamentSmokeTests` and `WireMatchSmokeTests`' SmokeB read
BgRLEngine's parity model in place (`ServerHarness.ParityModelPath`). Without
the sibling those two fail loud, naming the path; neither skips. The fixtures'
arrangement is BgInference's (its Build and test), and where they should live is
the open question of halheinrich/backgammon#455.

One golden is committed: `BgTournament.Tests/Goldens/export_completed_match.mat`,
compared byte-for-byte. `.gitattributes` holds `*.mat -text` so that it sits
outside all EOL processing; the comment there carries the record (ca28642,
halheinrich/backgammon#166).

Known flakes, each seen once and green on re-run. Report one by its test name:

- `WireMatchSmokeTests.SmokeC_FairMode_PlayersVerifyObservedRolls_AndTheFullStreamMatchesTheTranscript`
  forfeited under machine load: halheinrich/backgammon#445, to be checked
  against halheinrich/backgammon#486 (see Pitfalls, "A send failure is a
  disconnect").
- `MatExportEndpointTests.ExportEndpoint_Faulted_AbandonedWithInternalErrorReason`
  failed on a cold full-suite run: halheinrich/backgammon#487.
- `AdminAuthTests.Enforcing_CreatedMatch_JournalsActor` lost a Windows file-lock
  race in its directory cleanup: halheinrich/backgammon#176.

To run it for real, start the server and connect the reference bot:

```bash
dotnet run --project BgTournament.Server
```

The bot's commands are `ONBOARDING.md` §5. The server's ports are
`BgTournament.Server/Properties/launchSettings.json`'s; its settings and endpoints
are under "Admin HTTP surface and configuration".

## Depends on

- **BgDataTypes_Lib** — `Play`/`Move`, `BoardState` (`FromMop`,
  `IndexOfSamePlay`), `BoardPosition`, `CubeAction`/`CubeOwner`.
- **BgGame_Lib** — `IPlayAgent`/`ICubeAgent`, `GameState` (`FromPosition`,
  `OpponentView`, `Snapshot`), `MatchState.FromScores`,
  `GameSnapshot`/`MatchSnapshot`, `MatchRunner` + `SeededDiceSource` and
  `VerifiableDiceSource`, `DiceKey`, `AgentContractViolationException`,
  transcripts.
- **BgMoveGen** — `MoveGenerator.ResolvePlay` and `Hop`: the server's one
  wire-play resolver, fed by `WireMapping.ToHops`; `MoveGenerator.GeneratePlays`:
  the SDK's conforming reply and the reference bot's candidate set.
- **BgMatchFormat_Lib** — `MatExporter` and `MatchExport`: the server's
  `.MAT` export (`MatExportProjection`).
- **BgInference** (test-only) — the dogfooding smoke's real engine:
  `OnePlyPlayAgent` + `ThresholdCubeAgent` over BgRLEngine's parity model (see
  Build and test). Not a dependency of the shipped projects.

## Layout

Twelve projects under `BgTournament.slnx`: the seven below and the five of
Depends on. Repo-root `Directory.Build.props` (TFM, `TreatWarningsAsErrors`, XML
doc generation) and `Directory.Packages.props` (Central Package Management — no
inline `Version=`) govern this repo's seven.

**`BgTournament.Protocol/`** — the wire contract's .NET binding and its
bridge to the substrate types. Three areas:

- **Messages** — `ProtocolMessage`, the polymorphic base and its `"type"`
  discriminator registry; `QueryMessage` / `ReplyMessage`, the
  requestId-carrying bases; the handshake (hello, welcome, rejected), the
  three query/reply pairs (play, cube offer, cube response) and the match
  lifecycle notifications (started, ended).
- **Wire shapes** — hand-defined, never substrate types: the game state,
  move, cube owner and time-control shapes, the cube-action and match-end
  enums; `VerifiableDice`, the fair-dice algorithm id and commit-context
  format.
- **The single paths** — `WireProtocol`, the protocol version and the one
  (de)serialization path; `WireMapping`, the one set of wire ↔ substrate
  correspondences; `ProtocolSocket`, the frame rule (one text frame is one
  message, 64 KiB cap); `ImmutableCopy`, the one way a message takes in a
  list.

**`BgTournament.Api/`** — the admin HTTP contracts: public, and with no project
references today (see "Enum strictness" below). Six areas:

- **Requests and credentials** — the match, tournament and engine
  registration requests; `EngineAttestation`, provenance as declared;
  `EngineKeyGrant`, the show-once key envelope; `AdminApiKey`, the admin
  header name; `TimeControl`, the validated Fischer control.
- **Status and summaries** — `MatchStatus` / `TournamentStatus` (which the
  server uses too), the engine, match and tournament summaries, standings
  and schedule entries, the credential-free `RosterEntry`, and
  `ErrorResponse`, the typed body of every non-success response.
- **Replay and the live feed** — `GameReplay` and its endpoint envelope,
  `GameEntry` (the discriminated play / cube-offer / cube-response union),
  `GamePosition` (seat One's frame) and `PlayMove` (mover-relative), the
  seat, cube-owner, result and response enums; `LiveMatchEvent`, the
  live-feed union.
- **Audit** — `AuditEvent`, the arbitration timeline union (no boards, no
  moves), its endpoint envelope, and the forfeit-cause and decision-kind
  vocabularies.
- **Enum strictness** — `StrictJsonStringEnumConverter`, strict about token
  kind, not case, which the Api's enums and the server's journal shapes
  register. It is a copy of BgDataTypes_Lib's; reusing that one is
  halheinrich/backgammon#429 (Hal's direction, 2026-10-31, after the doc arc),
  and until then the Api takes no project reference.
- **Immutable lists** — `ImmutableCopy`, the one way a record takes in a
  list, and the field-order rule that goes with it (see Pitfalls).

**`BgTournament.Core/`** — the execution-blind tournament domain, zero
dependencies: `Tournament`, the round-robin aggregate (schedule, results,
tie-break ladder), with `TournamentFormat`, `ScheduledMatch` (a schedule row
and its derived seed) and `StandingsRow`.

**`BgTournament.Server/`** — the tournament host (`Sdk.Web`), every type
`internal`. Seven areas:

- **Host** — `Program.cs`: the `/engine` WebSocket endpoint and the admin
  HTTP endpoints, the records rehydrated before any endpoint serves;
  `appsettings.json` and its options types (`TournamentOptions`: the
  decision and handshake timeouts and the engine policy; `AdminOptions`;
  `PersistenceOptions`).
- **Admin identity** — `AdminApiKeys` (the validated key set),
  `AdminActor` (one request's identity), `AdminAuthenticationMiddleware`
  (the whole-surface gate, `/engine` exempt).
- **Roster** — `EnginePolicy` (Open or Registered), `EngineKeyCredentials`
  (key generation and the salted hash), `RosterService` (the roster and
  its one transition point).
- **The engine wire** — `EngineSocketEndpoint` (the handshake gate, the
  roster gate included), `EngineConnection` (the receive loop, one query in
  flight), `IEngineChannel` (the query seam tests fake), `EngineRegistry`
  (sessions by name), `RemoteEngineAgent` (the substrate agents over a
  channel, failing with `EngineFailureExceptions`' taxonomy, and resolving
  each play reply to the generator's candidate).
- **Hosting matches and tournaments** — `MatchService`,
  `TournamentService`, `MatchClock` (the per-match Fischer clock over the
  `TimeProvider` seam), `CountingDiceSource` (the roll index fair mode
  needs), `DecisionKind` (the server's decision vocabulary), `ForfeitCause`
  (the structured forfeit taxonomy).
- **Projections and the live feed** — `ApiMapping` (the one internal →
  Api projection), `ReplayProjection`, `MatExportProjection` (the `.MAT`
  export, through BgMatchFormat_Lib), `AuditProjection`; `LiveMatch` (the
  per-match cache and SSE broadcast) and `CompositeMatchObserver` (the
  runner's callbacks fanned to it and to the journal).
- **Persistence** — `Persistence/`, the durable records journal: an event
  union per journal kind (match, tournament, server, roster) and
  `JournalShapes`; `JournalCodec` (the schema versions and the one
  (de)serialization path); `JournalMapping` (the one set of substrate ↔
  journal correspondences); `IJournalStore` / `FileJournalStore` (one
  `.jsonl` per journal, a directory per kind under the data directory);
  `JournalWriter` and `JournalReader` (the write pump; the one read policy,
  trusted-prefix recovery included); a journal writer per kind; and
  `JournalRehydrator`, the startup fold.

**`BgTournament.EngineClient/`** — the .NET engine SDK and the reference
agents. Four areas:

- **The client** — `EngineClient` (connect, handshake, serve local agents;
  the fair-dice hook and clock-aware dispatch), `EngineIdentity`,
  `HandshakeRejectedException`.
- **Clocks** — the clock-aware agent interfaces (the opt-in clocked
  overloads), `MatchTimeControl`, `ClockReading`.
- **Fair dice** — `DiceVerification` (the pure verifier and its report),
  `DiceAuditRecorder` (the per-match observations it verifies).
- **Reference agents** — `RandomPlayAgent` (a uniformly random legal play,
  seed required), `PassiveCubeAgent` (never doubles, always takes).

**`BgTournament.ReferenceBot/`** — the runnable console host over the SDK,
the third-party door: `Program.cs` (parse, compose, serve), the validated
CLI options and their `UsageException` funnel, the `ReferenceBot`
composition seam, and `ExitCode`'s sysexits-style outcomes.

**`BgTournament.Tests/`** — xUnit over every project, with
`Microsoft.AspNetCore.Mvc.Testing` for in-process wire tests and a fake
`TimeProvider` for clocks. Areas: the byte-for-byte goldens (wire, admin
Api, journal; `PROTOCOL.md`'s examples among them) and their strictness and
round-trip edges; the three mappings; the server over `TestServer`, driven
through `ServerTestHarness`'s raw-wire `TestEngine` (handshake, roster,
admin identity, enforcement, play resolution, and the list, replay, live,
audit and `.mat` endpoints, the last against `Goldens/`); clocks and fair dice;
persistence and rehydration; the Core domain; the reference bot and its
host; and full wire smokes — matches, one of them BgInference's engine over
BgRLEngine's parity model, and a round-robin tournament (see Build and test).

## Architecture

### The JSON is the contract

`PROTOCOL.md` is the SSOT for wire semantics; the `BgTournament.Protocol`
assembly is its .NET binding. Three layers keep them honest: `GoldenWireTests`
pin every message's exact wire text, `ProtocolDocTests` re-serializes every
fenced example in `PROTOCOL.md` byte-for-byte, and `TreatWarningsAsErrors` over
generated XML docs keeps the DTOs self-describing.

`WireProtocol` is the only (de)serialization path, a deliberate encapsulation
of the System.Text.Json footgun where serializing through a concrete declared
type silently drops the `"type"` discriminator. A frame that is no well-formed
message fails as one `JsonException`; `ProtocolSocket` refuses a binary or
over-cap frame with `ProtocolViolationException`; `EngineConnection` turns
either into a violation. Reply enums are deliberately narrow per query
(`CubeOfferAction` vs `CubeResponseAction`), so an out-of-contract action fails
at the deserialization boundary, not in game logic.

The wire state reserves `xgid` and the server leaves it unset. Whether to fill
it from BgDataTypes_Lib's `XgidEncoder` is halheinrich/backgammon#488.

### The frame rule

The queried player always sees its own frame, in-proc and on the wire: one
convention, stated for engines in `PROTOCOL.md` §4 and for agents in BgGame_Lib
(Pitfalls, "Cube responders see their own frame"). What this repo does with it:
`WireMapping` is the only home of wire↔substrate field correspondences and is
strictly frame-preserving, so `GameState.OpponentView` is the single
re-expression anywhere in the system.

### Play encoding and resolution

Wire moves are `{from, to}` hops with hits deliberately not encoded: hits are
board-derived facts the server reconstructs. `PROTOCOL.md` §6 is the contract:
each encoded move is one legal single-die move, in any order and by any legal
route (Hal's rulings of 2026-09-28 on halheinrich/backgammon#273, Strict among
them; the protocol stays version 1).

The server keeps no play matcher of its own. `WireMapping.ToHops` reads a
reply's moves as BgMoveGen `Hop`s, which cannot express a hit mark, and
`RemoteEngineAgent` resolves them through `MoveGenerator.ResolvePlay`, the one
resolver; its semantics are BgMoveGen's ("ResolvePlay — mark-free hops,
resolved Strictly"). What it returns, the generator's candidate in its encoding
and with its hit marks, is the only play that goes downstream: `MatchRunner`
applies it, and the journal, replay, live feed and `.MAT` export record it. The
engine's spelling never leaves the agent. A reply that resolves to no play is
the engine's contract violation, raised where it is resolved (an `IllegalPlay`
`AgentContractViolationException`, so a ContractViolation forfeit); the runner
still validates the candidate it applies.

The SDK keeps its side: `EngineClient` sends a legal play its local agent
returns, in whatever encoding, as the generator's candidate it is the same play
as (`BoardState.IndexOfSamePlay`), whose moves are single-die moves, and sends a
play that is no legal play as written, for the server to judge.

`WireMapping.ToWireMoves` reads `Move`'s hit sign itself to write a play's
moves; it is booked with the other such consumers as
halheinrich/backgammon#314.

### Server shape

`EngineSocketEndpoint` gates the handshake: version, non-empty unique name, and
the roster (see "Registration/originality"), each rejection a named `rejected`
message. `EngineConnection` runs one receive loop per engine, enforces strictly
one in-flight query (id-correlated), serializes sends, tolerates exactly the
protocol's benign race (§9: a reply to the most recently abandoned query is
discarded), and closes the connection on any violation; its `Closed` task is
what the match host watches for disconnects.

`RemoteEngineAgent` carries the forfeit taxonomy: malformed or out-of-range
replies, and play replies that resolve to no play, become
`AgentContractViolationException` (decision-appropriate kind + seat, public
ctors); timeout, flag fall and disconnect become engine-name-carrying server
exceptions; external cancellation passes untranslated.

`MatchService` owns what the substrate deliberately does not: seat↔engine
attribution, the forfeit policy (forfeit the match), the proactive disconnect
watch (the first connection to end mid-match takes the forfeit, even while its
opponent is thinking), per-receiver `matchStarted`/`matchEnded` framing, and
in-memory record retention (the full `MatchResult` with transcripts for
completed matches). Aborted and Faulted matches end silently on the wire: v1 has
no vocabulary for them and never lies about a reason (`PROTOCOL.md` §11).

### Provably-fair dice (commit-and-reveal)

Unseeded matches run on verifiable dice (BgGame_Lib's `VerifiableDiceSource`).
The scheme is `PROTOCOL.md` §8's: the commitment on `matchStarted`, the reveal
on `matchEnded`, the algorithm, the commit-context format, roll indexing, and
what it proves and doesn't. Here:

- **Fair mode is the default.** Omitting the `POST /matches` seed selects it; an
  explicit seed keeps the reproducible, uncommitted `SeededDiceSource`
  (dev/repro), which sends none of the fair fields.
- **One string.** The algorithm id and context format are single-sourced in
  `Protocol/VerifiableDice`, so producer, verifier and goldens agree.
- **`playQuery.rollIndex`** comes from a `CountingDiceSource` wrapping the real
  source.
- **Verification is split.** The server produces; a pure
  `DiceVerification.VerifyMatchDice` (in EngineClient, callable by any C#
  client) checks the commitment and each observed roll and returns an immutable
  report. `EngineClient`'s optional `onDiceVerified` hook records observations
  and delivers the report, never throwing on failure: the policy on a cheating
  server is the consumer's.
- **Tournaments.** An unseeded tournament runs each scheduled match on its own
  generated key, and its seed drives no dice. A seeded tournament drives each
  match's dice with that match's derived seed (see "Tournament shape").

### Time controls (Fischer clocks)

`POST /matches` and `POST /tournaments` may carry a `timeControl`:
`BgTournament.Api.TimeControl`, a constructor-validated immutable value. A
dedicated JSON converter folds its constructor rejections into `JsonException`,
so an invalid control hits the host's standard 400 funnel instead of a binding
500. The clock semantics and wire shape are `PROTOCOL.md` §10's: debit and
credit, latency on the player's clock, the `matchStarted` announcement, the
per-query pool fields.

The match then runs on a per-match `MatchClock`, and the control **replaces**
the flat `DecisionTimeoutSeconds` guard for that match, because §10.2 lets a
large pool be spent on one long think. The remaining pool is the only
per-decision limit, enforced by a flag `CancellationTokenSource` armed with the
pool: an emptied pool cancels the in-flight query and surfaces as
`EngineFlagFallException`, folded like the other forfeit causes. A
tournament-level control governs every scheduled match, each on a fresh clock;
a flag fall folds as an ordinary win for the non-offender.

Clock logic reads time only through the DI `TimeProvider`, which makes every
clock test deterministic and single-sources the timestamp stream the
arbitration log's clock evidence reuses.

The SDK surfaces the clock to hosted agents that opt in through
`IClockAwarePlayAgent`/`IClockAwareCubeAgent`, detected by type per role. The
control is announced once per match, always and nullable:
`OnTimeControlAnnounced(null)` affirmatively means unclocked, because the flat
regime's timeout is server config the wire cannot tell the agent. Each clocked
decision arrives with a `ClockReading` of both pools; the flat regime delivers
no reading and never a sentinel, so the regime is said by shape.

Substrate `IPlayAgent`/`ICubeAgent` stay clock-free on purpose. If in-proc
clocked matches ever exist, a substrate decision-context parameter, with
`MatchRunner` growing clock knowledge of its own, is the recorded option.

### Tournament shape

One seam, mirrored one layer up from MatchRunner ↔ MatchService.
`BgTournament.Core` is an execution-blind, zero-dependency domain library.
`Tournament` owns the round-robin schedule: every pair meets `MatchesPerPairing`
times, seats alternating within a pairing so an even count balances the seats
exactly, and each match's seed is a SplitMix64 derivation of tournament seed +
match index, so a pairing's matches diverge against deterministic engines while
the whole tournament replays from one seed. Pairings and seats do not depend on
the seed. It folds winners into standings: wins, then head-to-head within the
tied group, then Sonneborn-Berger on wins, then seeding order, a total
deterministic order.

`TournamentService` owns execution. It claims every participant for the
tournament's duration, which is what keeps one-in-flight-query-per-engine an
invariant across rounds, and plays the schedule strictly in order through
`MatchService`'s hosting core (`CreateHostedMatch` + `RunHostedMatchAsync`,
claim-free; the standalone `POST /matches` path wraps the same core with
per-match claims). It reports each outcome as a plain winner: a forfeit is a win
for the non-offender, and a participant found disconnected when its match comes
up forfeits without play (no `matchStarted` was ever sent). A match that ends
with no winner to fold (Aborted/Faulted) halts the tournament with that status.
Resuming a tournament after a restart is deliberately not implemented, though
the journaled results would allow it; an interrupted tournament's finished
matches remain fully served records. Tournaments are invisible on the wire:
engines see only the usual per-match lifecycle.

### Admin API shape

`BgTournament.Api` is the second public contract (the first is the wire): every
shape that crosses the admin HTTP boundary, referenced by the Server and by
BgArena_Blazor. Its enums pin their strings per member
(`JsonStringEnumMemberName`), so hosts and consumers serialize with plain Web
defaults and zero converter configuration. `MatchStatus`/`TournamentStatus` are
at once the server's internal state and the public vocabulary: one enum, so an
internal status refactor is visibly a contract change.

Server-side, `ApiMapping` is the only home of server-internal → Api projections
(the admin counterpart of `WireMapping`), and `ApiGoldenTests` pins every
shape's exact JSON the way the wire goldens pin the protocol. Listings serve
creation order via a never-serialized monotonic `Sequence` on each record,
because the concurrent dictionaries have no order of their own.

### Admin HTTP surface and configuration

Every type in the Server is `internal`, so no XML doc reaches a consumer: this
table is the surface's reference. Shapes are `BgTournament.Api`'s, and errors
carry `ErrorResponse`.

| Endpoint | What it does |
| --- | --- |
| `GET /engines` | Connected engines (name, version, author, inMatch). |
| `POST /matches` | `{engineOne, engineTwo, matchLength, seed?, maxGames?, timeControl?}`. 404 unknown engine; 409 busy or same engine. |
| `GET /matches` | Every match record, creation order. |
| `GET /matches/{matchId}` | Record summary (status, winner, scores, forfeit info). |
| `GET /matches/{matchId}/games` | A terminal match's replay, partial if it was interrupted. 409 while running. |
| `GET /matches/{matchId}/live` | `text/event-stream`: snapshot → per-move → terminal. |
| `GET /matches/{matchId}/export.mat` | A terminal match as Jellyfish `.MAT` text (attachment). 409 while running. |
| `GET /matches/{matchId}/audit` | A terminal match's arbitration timeline. 409 while running; 404 if its journal cannot be read. |
| `POST /tournaments` | `{participants[], matchLength, matchesPerPairing, seed?, timeControl?}`. 404 unknown engine; 409 busy. |
| `GET /tournaments` | Every tournament record, creation order. |
| `GET /tournaments/{tournamentId}` | Status, standings, winner, per-match ledger (ids resolve on `GET /matches/{matchId}`). |
| `GET /roster` | Every roster entry, registration order. |
| `GET /roster/{name}` | One entry. 404 unknown. |
| `POST /roster` | `RegisterEngineRequest` → `EngineKeyGrant` (the show-once key). 409 duplicate name: roster names are unique forever. |
| `POST /roster/{name}/attestation` | Re-declare provenance (body: `EngineAttestation`). 404 unknown; 409 deactivated. |
| `POST /roster/{name}/rotate` | → `EngineKeyGrant` (a fresh key; the old one dies now). 404 unknown; 409 deactivated. |
| `POST /roster/{name}/deactivate` | Terminal: the key stops authenticating. 404 unknown; 409 if already deactivated. |

Unless noted, a request that fails validation is a 400. `matchLength` 0 is a
money session and requires `maxGames`; that sentinel, and the Jacoby rule the
API does not state, are halheinrich/backgammon#317.

Every endpoint above sits behind the API-key gate (the `X-Api-Key` header) when
`Admin:ApiKeys` is non-empty; a refusal is a 401 with `ErrorResponse`. `/engine`
is not gated by it: the handshake is its gate.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Tournament:DecisionTimeoutSeconds` | 30 | The flat regime's limit; a `timeControl` replaces it. |
| `Tournament:HandshakeTimeoutSeconds` | 10 | Applies in both regimes. |
| `Tournament:EnginePolicy` | `Open` | `Open`, or `Registered`: roster identities with valid `engineKey`s only. A typo fails the boot, never silently opens. |
| `Persistence:DataDirectory` | `data` | Under the content root. The journals live in `matches/`, `tournaments/`, `server/` and `roster/` beneath it, and fair-mode dice keys are escrowed there, so the directory shares the server's trust domain. |
| `Admin:ApiKeys` | empty | Named admin API keys, actor name → key value, plaintext: the same trust domain. Empty or absent serves the admin surface anonymously; any key makes one required. |

The boot warns when the admin surface serves anonymously, and under `Registered`
when the roster has no active engines.

### Admin identity (API keys + actor journaling)

Config-defined named API keys (`Admin:ApiKeys`) are presented per request in the
`X-Api-Key` header; `BgTournament.Api.AdminApiKey.HeaderName` is the header's
SSOT. One rule covers the whole surface (`AdminAuthenticationMiddleware`); only
`/engine` is exempt.

Enforcement is implicit in the configuration: any key configured means every
admin request must present a valid one, and an empty section means anonymous
service, the dev/smoke default, announced by a startup warning so fail-open is
never silent. A presented key is *always* validated: an unknown key 401s even on
an anonymously-serving server, so a client/server key mismatch fails loudly
instead of silently working unattributed. A broken key set (blank name or key,
two names sharing a value) fails the boot; key lookup is fixed-time
(`CryptographicOperations.FixedTimeEquals`).

The authenticated actor (the key's *name*, never the key) rides the request as an
`AdminActor` feature, and the create endpoints stamp it into the durable record
as an optional `createdBy` on the match and tournament `created` journal headers.
It enriches the existing headers rather than adding a parallel event family, and
it rides the existing schema version because its absence means exactly "no
authenticated actor", which is true of every file written before actor identity
existed, of anonymous creations, and of tournament-scheduled matches. Those carry
no actor of their own: the tournament header's actor plus the `matchStarted`
linkage is their chain of custody. `createdBy` is journal-only until a surface
needs it, and the wire sees none of this.

Refusals are evidence: every 401 writes a server-journal `adminRejected` event
whose reason is the exact `ErrorResponse` string, the handshake-rejection funnel
one gate over. A new event type is why `ServerSchemaVersion` is 2.

The gate is deliberately plain middleware rather than the ASP.NET Core
authentication framework: no policies, schemes or claims consumers exist to
justify the ceremony, and the framework's challenge path fights the
`ErrorResponse`-body contract. A second scheme or per-endpoint authorization is
the trigger to migrate.

### Registration/originality (the roster)

The engine roster is the WCCC-model attestation posture made durable.
Registration is an *administrative* act on the admin surface: an admin records
the engine's name and its provenance declaration (`EngineAttestation`: authors,
origin, derived-from, stored as declared; forensics stays a declared arbiter's
right with no code behind it), and the server issues a 256-bit engine key, shown
exactly once in the `EngineKeyGrant` response.

**At rest** the key is a salted SHA-256 hash under a per-credential scheme id
(`sha256-salted-v1`), deliberately *not* a stretched KDF: keys are
server-generated CSPRNG values, so stretching protects nothing and would put CPU
on every hello, a handshake DoS surface. The scheme field is the migration path.

**Roster-as-journal.** The roster's state is the startup fold of an append-only
registration history: `JournalKind.Roster`, one *segment per roster-mutating
boot* (the server-journal idiom, created lazily, so idle boots write nothing),
events `registered` / `attestation` / `credentialRotated` / `deactivated`, each
stamped with the acting admin. Per-boot segments, not one long-lived reopened
file, keep `IJournalStore`'s create-once contract *and* the damage policy intact:
a write-once file's torn tail is always a true tail, whereas reopen-and-append
would turn one boot's torn tail into the next boot's mid-file corruption.

Unlike the observer journals, `RosterJournal` writes and flushes each event
synchronously, to the operating system, before the state changes or the show-once
key is returned, and a write failure *fails the mutation*. That ordering is the
guarantee; a flush goes no further (see "The write path"). Runtime mutations and
the startup fold apply events through the one `RosterService.Apply` transition
path, so a rehydrated roster cannot differ from the live one.

Deactivation is terminal, and deliberately so: no event type can resurrect an
entry. If a deactivated engine ever needs a comeback, design it consciously
(fresh key? re-attestation? same name?) rather than adding a flag-flip event.

**Wire enforcement.** `engineKey` and its rules are `PROTOCOL.md` §3.1's. Here,
`Tournament:EnginePolicy` picks `Open` (default: anyone connects, the dev/smoke
mode BgArena's in-proc boot relies on) or `Registered` (roster identities only).
The deployment record, which settled `Open` for the beta, is
halheinrich/backgammon#6. The admin-key shape repeats one gate over: a *presented*
key is always validated whichever the policy, and the key resolves the roster
entry whose name the hello's `engineName` must match. The mismatch rejection does
not echo the roster name. Rejections ride the handshake funnel (`rejected`
message + `handshakeRejected` server-journal evidence, same reason string).

`Registered` with an empty roster boots with a loud warning and rejects
everything; that is reachable and repairable at runtime (register, then connect),
so it is not a startup failure. An unparseable policy value *is* (config binding
throws).

The EngineClient SDK takes the key as an optional constructor parameter,
deliberately not on `EngineIdentity`, which is loggable metadata while the key is
a secret. `wss://` termination is the transit answer (`PROTOCOL.md` §11 records
the bearer-token gap and the upgrade path).

### Replay shape

`GET /matches/{matchId}/games` projects a terminal match's retained transcripts
onto the replay contract (`ReplayProjection`). The substrate records each
decision in the on-roll player's frame, stamps every entry with its `OnRollSeat`
and derives attribution on the subtypes (`CubeTranscriptEntry.ActingSeat`,
`GameEndedTranscriptEntry.Winner`), so the projection does two host-side jobs:

1. *Seat attribution*: a plain read of the stamped facts, with no sequencing
   heuristics.
2. *Frame normalization*: every position is re-expressed in seat One's frame
   before it crosses the API, so viewers anchor each engine to one side of the
   board and never learn the frame rule. The flip round-trips through
   `GameState.OpponentView`, never a local mirror.

Play moves stay mover-relative on purpose: that is the frame standard backgammon
notation uses, and a viewer steps through served positions rather than applying
moves. The terminal transcript event is not an entry: outcome lives at game level
and the last position becomes `finalState`.

Only a running match answers 409 (the live feed is its surface). Every terminal
match serves its retained games: the full set for a Completed one, and the games
that finished before the break (retained on `LiveMatch`) for any other, with the
qualifying `MatchStatus` on the response so a partial list is self-describing.

### Live feed shape

`GET /matches/{matchId}/live` is a `text/event-stream` push of one match as it
plays, for the same viewers. `LiveMatch` is the per-match cache and broadcast
hub: an `IMatchObserver` (BgGame_Lib's per-move seam) written from the runner's
flow and read concurrently by SSE subscribers.

It is *non-disruptive by construction*. The substrate invokes observer callbacks
synchronously on the match loop and fails the run if one throws, so every
callback does only fast in-memory work under a short, essentially uncontended
lock and enqueues to channels; it never awaits, blocks or does I/O. A projection
bug cannot take the match down: callbacks are contained, and a failure *faults
the feed loudly* (subscriber streams close, a loud EOF) rather than leaving it
silently stale.

Subscribing is atomic snapshot-plus-register, so a late joiner's join-in-progress
snapshot and its live increments neither gap nor duplicate; an already-terminal
match takes the same one path (snapshot → terminal → close). The feed sends no
event ids and honors no `Last-Event-ID`: a dropped connection recovers by
re-subscribing, whose fresh snapshot re-establishes state. Subscriber channels
are unbounded, which holds because the subscribers are server-to-server, one UI
host at low event volume.

The event payloads are the replay contracts (`GameEntry` / `GameReplay`)
projected identically, the single projection SSOT feeding both surfaces, under a
small envelope union (`LiveMatchEvent`). A game's frame-free start context
(seat-absolute entering scores + the Crawford flag) rides `OnGameStarted`'s
`GameStartContext` straight from the substrate onto `gameStarted` and the
current-game `snapshot`; the feed holds those values verbatim rather than
re-folding a score tally of its own.

The one terminal event is emitted by the host (`MatchService`, after the outcome
is folded into the record), not the substrate, which emits nothing on abort; so
it fires once for every outcome and carries the truth. Spectating is invisible on
the wire.

### Export shape

`GET /matches/{matchId}/export.mat` serves a terminal match as Jellyfish `.MAT`
text, a `text/plain` attachment (`match_{matchId}.mat`). The bytes come straight
from `MatExporter.Export`, whose format and factories are BgMatchFormat_Lib's
("Input shape — `MatchExport`"), and are served verbatim. BgTournament.Api stays
out of it: this is text, not a JSON contract.

`MatExportProjection` is the counterpart of `ReplayProjection`. It maps the
record onto the exporter's factory choice: Completed length > 0 → `ForMatch`,
Completed length 0 → `ForMoneySession`, Forfeited → `ForForfeit` (the
non-forfeiting seat wins, read off the same `ForfeitedBy` taxonomy),
Aborted/Faulted/Interrupted → `ForAbandoned` (winner-less, with a one-line reason
from the status). Engine names map to Player 1/2 on the same absolute seat
convention as replay, and `matchId` rides a `Match ID` header tag.

The completed games are the retained replay source. A match that did not complete
also carries its trailing in-flight game, which `LiveMatch` retains as the
untouched substrate `Transcript`: the replay projections stay seat-One-frame API
shapes, and the export needs the raw substrate frame. Same terminal-only gate as
replay.

### Durable records (the journal)

Records survive restarts via an append-only, schema-versioned JSONL journal per
match and per tournament under `Persistence:DataDirectory`, designed as an
*ordered audit record* (the arbitration log reads this vocabulary), not a
cache-warming format. Every event carries an `at` timestamp from the DI
`TimeProvider`.

The first line is always the `created` header: identity, configuration,
`schemaVersion` and, in fair mode, the *escrowed* `DiceKey`, so that an interrupted
match can still reveal it. The header is the first event queued on the background
pump, which writes it before any later event, and nothing waits for it before play
starts: recovering an interrupted match's key depends on the header having been
flushed before the process died, and the damage policy then reads what remains. The
commitment stays derived, never stored.

Match events mirror the observer flow at full substrate fidelity (`started`,
`gameStarted`, `play`/`cube`/`gameEnded` transcript entries in their native
frames with hit encoding intact, unlike the wire), and a host-written `terminal`
(status, winner, scores, structured `ForfeitCause`) closes the file. The
tournament journal is thin: `created`, `matchStarted` (the schedule-index ↔
match-id linkage), one `result` per fold, `terminal`. Its schedule is re-derived,
never stored.

**The write path.** `MatchJournal` is a sibling `IMatchObserver` beside `LiveMatch`
under a `CompositeMatchObserver`, with the same discipline: a journal fault logs
loudly and stops journaling while the match plays on. It drains through a
per-journal background `JournalWriter`, so no caller waits on disk, the match
loop least of all. The pump writes and flushes each event out of process buffers
to the operating system, and no further. A process crash loses no event already
flushed; events still queued or not yet flushed can be lost if the process dies;
and this is no power-loss guarantee. Recovery applies the damage policy below to
whatever journal remains.

**The layering is strict SSOT.** Journal DTOs are their own family with
journal-local enums (`JournalCodec` the only (de)serialization path,
`JournalMapping` the only correspondences, `JournalGoldenTests` the byte pins),
and `IJournalStore`/`FileJournalStore` move raw streams only.

**The startup fold.** `JournalRehydrator` folds every journal back into records
*before endpoints serve*, replaying entries through a fresh `LiveMatch`'s own
callbacks (no second fold path) and rebuilding `GameRecord`s from the stamped
facts, so replay, `.MAT` export and summaries serve identically across a restart.
Restart listing order is header-timestamp order with an ordinal-id tiebreak.

**The damage policy.** A torn final line is dropped with a warning; corruption on
an earlier line folds the trusted prefix with a corruption-naming detail; a
journal with no trusted `terminal` event folds to `MatchStatus.Interrupted` /
`TournamentStatus.Interrupted`: terminal, evidence intact, `endedAtUtc` honestly
null. One gap as the code stands: a journalled board that is not a well-formed
position fails the fold of the whole file, which is logged and skipped, where the
policy would prefer line corruption. That is item 1 of
halheinrich/backgammon#315; item 3 is the record syntax a play violation's detail
prints its hops in.

### Arbitration log (the journal as evidence, and its read surface)

The durable journal doubles as the arbitration record. Match journals (schema v2;
v1 files fold forever) carry two evidence-only event types the fold ignores:

- `clock` — one per settled clocked decision, written from `MatchClock`'s
  settlement callback: seat, decision kind, measured think, increment credited,
  pool before and after. The clock stays the one measurement SSOT, and
  flat-regime matches measure nothing.
- `lateReply` — raised by `EngineConnection.LateReplyDiscarded` when the benign
  §9 race discards a reply to an abandoned query: proof the engine answered, too
  late to count.

Ordering is part of the vocabulary: a clock event precedes the entry it timed; a
`cubeOffer` clock event with no cube entry was a declined double window (think
time replay elides); a trailing clock event is the decision that ended the match;
a dance is a play entry with no clock event (never queried).

The **server journal** (`server/<id>.jsonl`, one segment per boot, independently
versioned) records engine lifecycle: a `started` header,
`engineConnected`/`engineDisconnected`, `handshakeRejected` with the exact wire
reason (one funnel in `EngineSocketEndpoint`), `adminRejected` (see "Admin
identity"), and a graceful `stopped` marker whose absence is crash evidence. It is
deliberately **evidence-only**: sessions are ephemeral, and nothing rehydrates
from it.

The read surface is `GET /matches/{matchId}/audit`: `BgTournament.Api` audit DTOs
projected by `AuditProjection` over the journal's trusted prefix (`JournalReader`,
the parse-policy SSOT shared with rehydration) at **arbitration altitude**:
timestamps, attribution, causes, clock arithmetic; no boards or moves. Game number
+ entry index join to replay.

The structured `ForfeitCause` rides the terminal audit event, which is
**record-derived** (skipping the journal's terminal line): identical content for a
folded terminal, and the only truthful close for Interrupted, with `at` honestly
null and the escrowed fair-dice key revealed. Fair mode makes the response a
self-contained verification packet: `created` carries the derived commitment
(`MatchRecord.Commitment`, the wire's own derivation) + algorithm, play events
carry the rolls, `terminal` reveals the key.

Refusals mirror replay, and the endpoint awaits `MatchRecord.JournalSettled`,
completed unconditionally on every finalize path, so a read never races the
journal drain.

### Client shape

`EngineClient.ServeAsync(WebSocket)` is the transport seam (any socket source,
in-proc test servers included); `RunAsync(Uri)` wraps it with a `ClientWebSocket`.
There is no perspective work client-side: the frame rule means query states map
straight onto the local agent contract. Play replies conform to `PROTOCOL.md` §6
whatever encoding the local agent returns (see "Play encoding and resolution"). A
local agent exception drops the connection, which the server correctly reads as a
forfeit. Every clocked query's pools are logged at `Debug`, so the reference bot's
`--verbosity debug` showcases the clock with no bot code involved.

### Reference-bot executable

`BgTournament.ReferenceBot` is the SDK made runnable, the third-party door as an
actual process: a thin `Program` over `EngineClient` + the reference
`RandomPlayAgent`/`PassiveCubeAgent`, driven by validated command-line options. It
is at once the baseline opponent, a connection smoke test, and, as tested source,
the canonical worked example for engine authors. It is an application, not a
library: all its types are internal, and its API is the command line:

```text
BgTournament.ReferenceBot --server <ws|wss uri> --name <name>
    [--seed <int>] [--version <s>] [--author <s>] [--engine-key <hex>]
    [--verbosity trace|debug|information|warning|error|critical|none] [--help]
```

Exit codes (`ExitCode`): 0 served, 64 usage, 68 connection, 69 handshake rejected,
130 interrupted. `ONBOARDING.md` §5 walks competitors through it.

Everything worth testing sits behind a composition seam:
`ReferenceBot.CreateClient` (assemble the client), `ReferenceBotOptions.Parse`
(the CLI → `UsageException` funnel) and the pure `DescribeDiceReport`, which the
in-proc smoke drives directly over a TestServer socket, exactly the composition
`Program` connects to the wire. `Program` itself only wires args to that seam,
prints the fair-dice report through the SDK's `onDiceVerified` hook, and maps every
failure to a named `ExitCode` rather than a stack trace. User-facing output is
plain `Console` (usage and errors to stderr, report to stdout); the SDK's
diagnostics flow through a console `ILogger` filtered by `--verbosity`, so log
configuration never holds the showcase output hostage.

## Pitfalls

### The wire

- **Serialize only through `WireProtocol`.** Don't add parallel serialization
  paths (see "The JSON is the contract"). The helper's base-typed parameter makes
  the dropped discriminator structurally hard to hit.
- **A wire-text change is a protocol change.** The golden tests and
  `ProtocolDocTests` pin every byte on purpose. If a refactor (or a substrate
  rename reached through `WireMapping`) fails them, that is the alarm working:
  either fix the regression, or consciously version the protocol and move
  doc + goldens together.
- **No flips in `WireMapping`, ever.** A flip in the mapping double-applies the
  frame rule. The pin: `ToWireState_OfOpponentView_IsTheResponderFrame_NoDoubleFlip`.
- **Every play reply goes through `MoveGenerator.ResolvePlay`, and only its
  candidate goes downstream.** Don't add a matcher here, don't validate
  replies with `IsLegalPlay`, and never apply or record a play spelled from
  the reply: BgMoveGen's Pitfalls say why each fails (its `ResolvePlay`
  entries). A reply the resolver refuses is the engine's violation, raised in
  `RemoteEngineAgent`; don't hand it to the runner for a verdict.
- **The pass needs no special case.** An empty `moves` array resolves when
  passing is the only legal play, by `ResolvePlay`'s own contract.
- **One in-flight query per engine is an invariant, not a hope.**
  `EngineConnection.QueryAsync` throws `InvalidOperationException` on overlap
  (a server bug); it holds because an engine is in at most one match (the
  registry busy flag) and `MatchRunner` is sequential.
- **A forfeit's `matchEnded` reports 0–0.** Partial scores are lost when the
  runner throws (halheinrich/backgammon#485); the replay, journal and `.MAT`
  export keep the finished games.
- **`EngineConnection.CloseAsync` is a half-close on purpose.** It sends the
  close frame (`CloseOutputAsync`) without waiting for the peer's ack: a
  misbehaving engine may never send one, and `WebSocket.CloseAsync` would
  hang the match run's `finally` forever (which the tournament loop awaits).
  Don't "upgrade" it back to a full handshake.
- **A send failure is a disconnect, by translation.** `EngineConnection.SendAsync`
  converts transport-level send errors into `EngineDisconnectedException`, so a
  closing-but-not-yet-observed connection forfeits cleanly instead of faulting
  the match. The tournament loop's connected pre-check is best-effort on top of
  this, not load-bearing. The known gap: an engine lost while `matchStarted` is
  being sent can end Faulted (halheinrich/backgammon#486).

### Dice and seeds

- **Omitting the seed means fair mode, not a random seeded match.** Any test that
  wants deterministic dice must pass an explicit seed.
- **A fair-mode seed is a recorded datum, not a reproduction key.** It stays on
  `MatchRecord.Seed`, `MatchSummary.Seed` and `TournamentMatchEntry.Seed`, but the
  committed `DiceKey` drives and reproduces the dice.
- **The seed derivation is a reproducibility and a durable-format contract.**
  Changing `Tournament.DeriveMatchSeed` (or the schedule construction) silently
  changes every tournament's seeds, a seeded tournament's dice with them, and what
  rehydration rebuilds: the tournament journal stores only participants + format +
  seed and *re-derives* the schedule,
  so a derivation change re-folds every stored tournament differently even though
  no JSON byte moved. It needs the same conscious versioning thought as a journal
  shape change.
- **`rollIndex` leans on the runner's roll-then-query sequencing.**
  `CountingDiceSource` counts `Roll()` calls; `RemoteEngineAgent` stamps
  `rollIndex = RollsProduced - 1`, correct only because `MatchRunner` takes
  exactly one roll immediately before each play query (`PROTOCOL.md` §8.2 defines
  which events consume indices). The fair-mode smoke re-derives the whole stream
  against the transcript, so a sequencing regression fails loudly. Don't
  stamp the index anywhere but at play-query time.
- **The fair-dice algorithm id and commit context are frozen public contract.**
  `Protocol/VerifiableDice` is their one home; a change breaks every external
  verifier and the published cross-language vectors (`PROTOCOL.md` §8.3).
  `DiceCommitment.Verifies` and the context string must match byte-for-byte across
  producer and verifier or verification silently fails.
- **The derived dice commitment must stay derived.** The audit `created` event's
  commitment comes from `MatchRecord.Commitment` (the same `DiceKey.Commit` +
  context path `matchStarted` used). Never store it in the journal or derive it a
  second way; the no-drift pin is the wire↔audit byte equality in
  `AuditEndpointTests`.

### Clocks

- **Don't reintroduce the flat guard, or ambient time.** Under a `MatchClock` the
  remaining pool is the only per-decision limit, and a second `CancelAfter` in the
  clocked bracket would silently change match outcomes. `MatchClock`, and anything
  that joins it, reads time only through the injected `TimeProvider`
  (`GetTimestamp`/`GetElapsedTime`, and the flag CTS's TimeProvider constructor).
  Why: "Time controls".
- **Only answered decisions earn the increment, settled exactly once.**
  `MatchClock.Decision` debits measured time on dispose (idempotent) and credits
  the increment only if `MarkAnswered()` ran first; a flag fall, violation,
  disconnect or external cancellation debits without credit. Keep `MarkAnswered`
  after the exchange returns, never before.
- **The flat regime has no pools, and the SDK says so by shape.** Never fabricate
  a `ClockReading` or a sentinel (no infinite, zero or negative pools an agent
  might do arithmetic on), and don't "fix" the announcement to fire only when
  clocked: its null is the affirmative unclocked signal. A query carrying only one
  of the two pool fields is out of contract and deliberately treated as unclocked
  (`EngineClient.ClockOf`). The SDK does not "correct" a reading for latency
  (`ClockReading`'s doc).
- **Clock-awareness is detected per role object; the announcement dedupes by
  reference.** `EngineClient` type-tests the play and cube agents separately, so one
  can be aware while the other stays plain (`TimeControlWireTests` pins a dual-role
  object; no test pins that mix), and one object serving both roles hears each
  match's announcement exactly once (`ReferenceEquals` in `AnnounceTimeControl`).
- **Clock evidence obeys the ordering vocabulary, and only clocked matches have
  it.** Audit readers rely on it (see "Arbitration log"). Don't journal the
  settlement anywhere but the `MatchClock` callback, and don't invent flat-regime
  timing.

### Tournaments

- **A tournament binds the sessions present at start.** A participant that
  disconnects, or is kicked by a forfeit close, forfeits its remaining matches
  without play, even if it reconnects under the same name. Both sides gone halts
  the tournament as Faulted (a match can be forfeited to no one).
- **`Tournament` (Core) is not thread-safe.** The server serializes every
  aggregate read and mutation through its `TournamentRecord.Gate`; keep any new
  access inside that lock.

### Observers, replay and the Api

- **Match-loop observer callbacks stay non-throwing and non-blocking.** This binds
  `LiveMatch` and `MatchJournal` alike (see "Live feed shape"): fast in-memory work
  and a non-blocking channel write, nothing else. Never await, do I/O, take a
  contended lock or throw uncontained in one; `SafelyObserve` logs and faults the
  feed loudly instead. Disk work happens only on the `JournalWriter` pump, and a
  journal failure logs loudly and stops journaling: the match must play on, and
  the record must never wait on disk.
- **The terminal live event comes from the host, through
  `LiveMatch.MarkTerminal`, never the observer callbacks:** an aborted run gets no
  substrate callback at all.
- **Attribution is the substrate's, read not re-derived.** `ReplayProjection`
  reads the stamped facts; don't reintroduce a sequencing heuristic (opening-die
  winner, flip-per-play, cube-no-flip), which would be a second source of truth.
  The flip must stay a `GameState.OpponentView` round-trip, and
  `ProjectEntry`/`ProjectGame` are the single projection SSOT the live feed reuses:
  a change is felt on both surfaces at once.
- **The admin JSON is pinned byte-for-byte, wire-style.** `ApiGoldenTests` is the
  alarm: a shape or enum-string drift in `BgTournament.Api` is a contract change
  for BgArena_Blazor. The Web-default encoder escapes apostrophes (`'` → `\u0027`)
  in detail strings, pinned deliberately.
- **Every list a public shape holds is an immutable copy, and a redeclared
  list property moves in the JSON.** No wire message or Api record hands out
  a caller's array or a deserializer's `List<T>` behind `IReadOnlyList<T>`
  (the collection rider of halheinrich/backgammon#273, Hal's ruling of 2026-09-26,
  pinned by `CollectionImmutabilityTests`): each copies through its assembly's
  `ImmutableCopy` in the property's `init` and, for a positional record, in
  the property's initializer. Redeclaring a positional property puts it
  after the synthesized ones in the serializer's field order, so where it is
  not last, `[JsonPropertyOrder]` keeps the order `ApiGoldenTests` pins; a
  new list member on a positional record needs the same care. The copy does
  not validate: a null stays null, for the server's own validation to refuse.
  `ImmutableCopy` exists in two assemblies here and in two other members; one
  decision or a shared shape is halheinrich/backgammon#316.

### The journal

- **The journal format is durable: a byte change orphans files already on disk.**
  `JournalGoldenTests` pins every event and enum string; a deliberate change bumps
  `JournalCodec.SchemaVersion` and decides the migration story consciously.
  Rehydration accepts the [`MinSchemaVersion`, `SchemaVersion`] range, so old files
  fold forever, and skips unknown versions loudly. The server and roster journals
  version independently (`ServerSchemaVersion`, `RosterSchemaVersion`): they are
  separate durable formats.
- **A new event type is a schema change here, not a wire-style additive field.**
  The codec is deliberately strict, so an unknown discriminator under an old stamp
  reads as corruption to an old reader. That is why match schema v2 exists (the
  `clock` and `lateReply` evidence events); the wire's additive-under-v1 precedent
  does not transfer.
- **An optional field may ride the current version only if its absence keeps
  meaning the truth for already-written files.** `createdBy` is the precedent (see
  "Admin identity"); anything else bumps.
- **Journal DTOs use journal-local enums, never the Api or substrate ones.** An Api
  rename must never silently rewrite the durable format. Serialize, map and parse
  only through `JournalCodec` (the same discriminator footgun as `WireProtocol`),
  `JournalMapping` and `JournalReader` (the one parse policy, shared by rehydration
  and audit).
- **Journal moves keep the hit signs the wire strips.** `JournalMapping` serializes
  `Play` moves with the raw sign-encoded destinations (negative = hit, 0 = bear-off)
  so rehydration rebuilds the exact substrate `Play`. Reusing
  `WireMapping.ToWireMoves` here would silently lose hits from every stored
  transcript: the rebuilt plays would not be the ones the runner applied (what makes
  a play valid from its position is `BoardState.IsSamePlay`'s doc comment), and the
  audit record would lie.
- **The damage policy is tail-lenient, prefix-strict.** Only a journal's *final*
  unparseable line is a torn tail; anything earlier is corruption and the fold stops
  there. Never trust events past a corrupt line, even a well-formed terminal.
- **Interrupted is rehydration's word alone.** No code path ever *writes* an
  interrupted terminal event: the status is precisely "the journal has no trusted
  terminal line". A shutdown race can therefore demote Aborted to Interrupted on the
  next boot (terminal written but not yet flushed when the process died). Both are
  honest "server stopped it" statuses; don't "fix" this by writing terminal events
  early.
- **`Restore` runs before the endpoints serve, once.** Rehydrated records are
  terminal, `MarkTerminal`'d and journal-less (`Journal` null: the file on disk
  already is their complete history). Nothing may rehydrate to Running, and nothing
  may append to a rehydrated journal.
- **The server journal is evidence-only: never rehydrate it.** The `stopped`
  marker's *absence* is the crash evidence, so never write it anywhere but graceful
  shutdown.
- **The terminal audit event is record-derived; the audit read waits on
  `JournalSettled`.** Don't re-read the terminal from the file, and don't remove the
  endpoint's `await record.JournalSettled`: a match turns terminal before its
  journal drains, and without the gate an immediate read serves a short file.
  `MarkJournalSettled` completes **unconditionally** on every finalize path: a gate
  left pending turns a millisecond race into a hung request, strictly worse than the
  short read it prevents.

### Keys, actors and the roster

- **A presented key is always validated, admin or engine, whichever the mode.**
  Don't "fix" an unknown key's 401 into tolerance, and don't add a separate enforce
  flag: key presence *is* the switch, which makes an enforce-without-keys
  misconfiguration unrepresentable. Open mode only means a *keyless* hello is
  admitted. An engine key authenticates the claimed name: a valid key with a
  mismatched `engineName` is rejected, never silently renamed, because silent
  adoption would fork the engine's own logs from the server's record, an attribution
  nightmare in arbitration.
- **Never journal or log a presented key value, admin or engine.** Refusal evidence
  records the reason, method and path only (a mistyped *real* secret must not land
  in a durable file), the actor stamp is always the key's name, wire rejections
  never echo the presented `engineKey`, and the plaintext engine key exists exactly
  once, in the `EngineKeyGrant` response.
- **Scheduled matches deliberately carry no `createdBy`.** Propagating the
  tournament's actor onto each match header would fabricate N direct requests that
  never happened, and duplicate one recorded decision (see "Admin identity").
- **Roster segments are write-once, and never pruned casually.** Never add
  reopen-and-append (the reason is under "Registration/originality"): the roster
  would silently lose everything appended after a torn line. The fold across
  segments is ordered by segment header time (ordinal-id tiebreak), and roster events
  carry whole state, so a corrupt segment's dropped suffix leaves stale-but-honest
  entries the admin repairs by rotation. The roster's state is the segments' fold, so
  deleting one rewrites history.
- **`RosterJournal` is synchronous and throwing on purpose.** The `JournalWriter`
  discipline exists for observer callbacks on the match loop; roster mutations are
  admin-paced and their write order is load-bearing. Don't "fix" the roster onto
  `JournalWriter`: a dropped registration event with an issued key is an engine that
  can never authenticate.
- **Don't mutate roster entry state anywhere but `RosterService.Apply`.** The
  transitions living once is what guarantees a restart cannot re-fold a different
  roster.
- **The credential scheme is deliberately unstretched: don't "harden" it with a
  KDF** (see "Registration/originality"). A new scheme is new data under the
  per-credential `scheme` field, not a format break.
- **`Registered` + empty roster is a warned boot, not a failed one.** Don't turn the
  warning into a failure: the no-restart bootstrap (boot → register → connect)
  depends on it. Don't soften the opposite case either: an *unparseable* policy value
  fails the boot so that a typo can never silently open the wire.

### Tests and the build

- **In wire tests, await the client connect in the test body.** A fire-and-forget
  connect inside a background task swallows the real failure and surfaces as an
  unrelated registration timeout.
- **The reference bot's `Program` stays namespaced: don't "simplify" it to top-level
  statements.** The Tests project references both it and `BgTournament.Server`, whose
  top-level `Program` lives in the global namespace and anchors
  `WebApplicationFactory<Program>`. A second global `Program` makes that reference
  ambiguous (CS0104). The class/namespace collision with the SDK's `EngineClient`
  type is why the bot aliases it (`EngineClientSdk`) and its tests alias the
  composition seam (`Bot`).
