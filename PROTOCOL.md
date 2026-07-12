# BgTournament Wire Protocol — Version 1

This document is the canonical engine contract for playing matches against a
BgTournament server. It is deliberately language-neutral: everything an engine
author needs is here, in JSON terms. The C# types in `BgTournament.Protocol`
are a convenience binding of this document, not the contract itself; the
`GoldenWireTests` suite pins every example below byte-for-byte.

An engine is a program that connects, identifies itself, and answers decision
queries: *choose a play*, *double or not*, *take or pass*. Nothing else. The
server owns the dice (seeded, auditable), the rules, the state, and the
refereeing — an engine never rolls, never applies a move, never keeps
authoritative state.

## 1. Transport and framing

- Transport: **WebSocket**. The server exposes a single engine endpoint
  (path `/engine` on the reference server; deployment-specific).
- One **complete WebSocket text message = one JSON object = one protocol
  message**, UTF-8. (WebSocket-level fragmentation of a message is a transport
  detail; JSON never spans messages, and one message never carries two JSON
  objects.)
- Messages are small; the server may reject text messages larger than 64 KiB.
- No application-level heartbeat. Standard WebSocket ping/pong control frames
  may be used by either side.

## 2. Conventions

- Field names are **camelCase**.
- Enumerated values are **camelCase strings** (`"noDouble"`, `"take"`).
  Integer encodings of enum values are rejected.
- Optional fields are **omitted** when absent — never sent as `null`.
- Receivers **ignore unknown fields** (this is how future minor additions stay
  compatible). Unknown message *types* from an engine are protocol violations.
- The `"type"` discriminator is conventionally first, but receivers accept it
  in any position within the object.
- `requestId` is an **opaque string**, unique per query on a connection. Echo
  it verbatim; never parse or fabricate one.
- **At most one query is outstanding per engine at any time.** The protocol is
  a strict alternation: the server asks, you answer. An engine can be written
  as a simple read–decide–reply loop.

## 3. Handshake and versioning

The first message on a new connection must be the engine's `hello`. The server
answers `welcome` (registered; you may be seated in matches) or `rejected`
(after which the server closes the connection). Rejection reasons include a
protocol-version mismatch, an engine name already connected, or a malformed
hello. The server also rejects connections that send no hello within its
handshake timeout (default 10 seconds).

`protocolVersion` is an integer. This document describes **version 1**. There
is no negotiation: if the versions differ, the server rejects and names both
versions in the reason.

Engine → server:

```json
{"type":"hello","protocolVersion":1,"engineName":"MyBot","engineVersion":"2.1","author":"Jane Doe"}
```

Server → engine, one of:

```json
{"type":"welcome","protocolVersion":1}
{"type":"rejected","reason":"protocol version 2 not supported; this server speaks 1"}
```

`engineName` is your registry identity — matches are started by naming two
connected engines. `engineVersion` and `author` are optional labels for
records and diagnostics.

After `welcome`, the engine stays connected and idle until the server seats it
in a match. It may play any number of matches over one connection.

### 3.1 Registration and `engineKey` (optional)

A server may run one of two engine policies:

- **Open** — any engine may connect (the behavior described above; the
  reference server's default).
- **Registered** — only engines registered on the server's roster may
  connect. Registration is an administrative act on the server side: an
  operator records the engine and its provenance declaration, and the server
  issues a secret **engine key** — shown exactly once at registration; the
  server retains only a salted hash. Key delivery to the engine author is
  out-of-band (not part of this protocol).

A registered engine presents its key as an optional `engineKey` field on the
hello (additive under §2's unknown-field rule — the protocol stays version 1,
and an unregistered engine sends exactly the hello it always did):

```json
{"type":"hello","protocolVersion":1,"engineName":"MyBot","engineKey":"5f2c9a1d3e8b4c6f0a7d2e9b1c4f8a3d6e0b5c2f9a4d7e1b8c3f6a0d5e2b9c4f"}
```

Rules, both policies:

- A **presented key is always validated**, even by an Open server: an
  unrecognized key is rejected, never silently ignored — a key mismatch
  between engine and server must fail loudly, not degrade to an anonymous
  connection.
- The key resolves the roster identity, and `engineName` must equal that
  identity's registered name; a disagreement is rejected. The key
  authenticates the name — it does not rename the engine.
- A key belonging to a deactivated roster entry is rejected.

Under **Registered**, a hello without an `engineKey` (or with an invalid one)
is rejected with a named reason. Under **Open**, a keyless hello connects as
before.

The key is a bearer secret in a plaintext field: on untrusted networks,
terminate the connection over `wss://` (TLS) — transport security is a
deployment concern, not a protocol change.

## 4. The frame rule

**Every state you receive is expressed in your own frame.** Positive board
values are your checkers, `yourScore` is your score, `cubeOwner: "you"` means
you own the cube. You never need to ask "which side am I this time?" — you are
always the positive side of whatever you are sent.

The same physical position looks different to the two players. A checker your
opponent sees at their point `p` with count `-n`, you see at point `25 - p`
with count `+n`. A checker on **your** bar is `board[25] = +1` in your frame
and `board[0] = -1` in your opponent's.

Who is *on roll* depends on the query:

- `playQuery`, `cubeOfferQuery`: **you** are on roll.
- `cubeResponseQuery`: **your opponent** is on roll — they doubled before
  rolling, and they roll next if you take. The state is still in your frame.

### The cube in a response query

At response-query time the double is *pending*, so the state shows the
**pre-double cube**: its pre-double value, owned by `"centered"` (first double
of the game) or `"opponent"` (a redouble by the opponent, who owned the cube).
It is never `"you"` — a player can only double a cube they have access to. If
you take, the cube becomes yours at twice the shown value.

Worked example — you are Alpha, playing Beta. Beta took an earlier double, so
Beta owns the cube at 2. Beta now redoubles to 4. You receive:

```json
{"type":"cubeResponseQuery","requestId":"q-43","state":{"board":[0,2,2,2,2,2,3,0,0,0,0,0,0,0,0,0,0,0,0,-2,-2,-3,-2,-2,-2,0],"cubeValue":2,"cubeOwner":"opponent","matchLength":7,"yourScore":2,"opponentScore":3,"isCrawford":false}}
```

Reading it: the cube shown is the pre-double cube (value 2, Beta's). Your
checkers (positive) are in your home board; Beta rolls next if you take. Reply
`take` and play continues with the cube at 4, owned by you; reply `pass` and
you concede this game at the pre-double stake of 2 points.

## 5. The state object

Carried by every query as `state`:

| Field           | Type      | Meaning                                                                 |
| --------------- | --------- | ----------------------------------------------------------------------- |
| `board`         | int[26]   | The position, in your frame (below).                                    |
| `cubeValue`     | int       | Doubling-cube value: 1, 2, 4, 8, …                                      |
| `cubeOwner`     | string    | `"you"` \| `"opponent"` \| `"centered"` — who may offer the next double. |
| `matchLength`   | int       | Match length in points; `0` = money session.                            |
| `yourScore`     | int       | Your match points so far.                                               |
| `opponentScore` | int       | Opponent's match points so far.                                         |
| `isCrawford`    | bool      | True iff this game is the Crawford game (cube suspended).               |
| `xgid`          | string?   | Optional debug decoration; MAY be present. Never rely on it.            |

**Board layout.** `board` has 26 integers:

- `board[0]` — your opponent's bar.
- `board[1..24]` — the points, numbered from **your** bear-off end: your home
  board is points 1–6, your opponent's home board is points 19–24.
- `board[25]` — your bar.

Each entry is a signed checker count: positive checkers are yours, negative
are your opponent's. You move from higher-numbered points toward lower ones;
you bear off from points 1–6; you enter from the bar onto points 19–24 (an
entry with die *d* lands on point `25 - d`). Checkers borne off do not appear
on the board — they are implied by the counts summing to fewer than 15 a side.

## 6. Decision queries and replies

### playQuery → playReply

You are on roll with the given dice; choose your play.

```json
{"type":"playQuery","requestId":"q-17","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered","matchLength":7,"yourScore":0,"opponentScore":0,"isCrawford":false},"die1":3,"die2":1}
```

That is the standard opening position with an opening roll of 3-1. This reply
plays 8/5 6/5 (one checker from your 8-point using the 3, one from your
6-point using the 1, making your 5-point):

```json
{"type":"playReply","requestId":"q-17","moves":[{"from":8,"to":5},{"from":6,"to":5}]}
```

**Move encoding.** Each move is `{from, to}` in your frame: `from` is 1–24, or
25 to enter from your bar; `to` is 1–24, or 0 to bear the checker off. A turn
has 0–4 moves (up to 4 when you roll doubles). Order does not matter. **Hits
are not encoded** — if your move lands on an opponent blot, the server derives
the hit. Moving one checker twice (e.g. 24/21 21/20 with a 3-1) is expressed
as two moves.

**Legality.** The server is the sole legality authority (standard backgammon;
no variants in version 1). Your reply must be one of the legal plays for the
queried position and dice — including the obligations to use both dice when
possible and the larger die when only one can be used. If no legal play
exists, reply with an empty `moves` array:

```json
{"type":"playReply","requestId":"q-18","moves":[]}
```

(The server may also skip you entirely for closed-out dance turns — you simply
won't be queried.) An illegal or unresolvable play forfeits the match; see §9.

**Roll index (fair mode).** When the match runs on provably-fair dice (§8), each
`playQuery` also carries `rollIndex` — this roll's 0-based position in the
match's dice stream — so you can verify the roll against the committed key once
it is revealed. It is omitted for explicit-seed matches. See §8 for the recipe.

### cubeOfferQuery → cubeOfferReply

You are on roll and doubling is legal for you (never sent in Crawford games,
in 1-point matches — which are cubeless — or when your opponent owns the
cube). Decide before your roll.

```json
{"type":"cubeOfferQuery","requestId":"q-42","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered","matchLength":7,"yourScore":3,"opponentScore":2,"isCrawford":false}}
```

```json
{"type":"cubeOfferReply","requestId":"q-42","action":"double"}
```

`action` is `"noDouble"` or `"double"`.

### cubeResponseQuery → cubeResponseReply

Your opponent doubled; take or pass. See §4 for the frame and cube semantics
and a worked example.

```json
{"type":"cubeResponseReply","requestId":"q-43","action":"take"}
```

`action` is `"take"` or `"pass"`.

## 7. Match lifecycle notifications

Notifications are server → engine, carry no `requestId`, and require no reply.

```json
{"type":"matchStarted","matchId":"m-1","opponent":"RandomBot","matchLength":7}
{"type":"matchStarted","matchId":"m-2","opponent":"RandomBot","matchLength":0,"maxGames":50}
```

`matchLength: 0` is a money session; `maxGames` is present when a games cap is
configured (always, for money sessions).

```json
{"type":"matchEnded","matchId":"m-1","reason":"matchComplete","yourPoints":7,"opponentPoints":4,"youWon":true}
{"type":"matchEnded","matchId":"m-2","reason":"gamesCapReached","yourPoints":11,"opponentPoints":9}
{"type":"matchEnded","matchId":"m-1","reason":"forfeit","yourPoints":3,"opponentPoints":7,"youWon":false,"forfeitedBy":"you","detail":"reply to playQuery was not a legal play for the queried dice"}
```

`reason` is `"matchComplete"`, `"gamesCapReached"`, or `"forfeit"`. `youWon`
is omitted when there is no winner (money sessions; a games cap reached with
no side at the match length). `forfeitedBy` (`"you"` / `"opponent"`) and the
optional human-readable `detail` accompany forfeits.

When the server runs the match on **provably-fair dice** (§8), `matchStarted`
additionally carries `diceCommitment` and `diceAlgorithm`, and `matchEnded`
carries the revealed `diceKey`:

```json
{"type":"matchStarted","matchId":"m-1","opponent":"RandomBot","matchLength":7,"diceCommitment":"adc77c694eef819749968e4d9c7e479af9afecd7db819bab37d5bcdb7b34554e","diceAlgorithm":"hmac-sha256-dice-v1"}
```

```json
{"type":"matchEnded","matchId":"m-1","reason":"matchComplete","yourPoints":7,"opponentPoints":4,"youWon":true,"diceKey":"0000000000000000000000000000000000000000000000000000000000000000"}
```

These fields are absent for explicit-seed matches. An engine that ignores them
plays exactly the same — verifying the dice is optional (§8).

When the match runs under a **time control**, `matchStarted` additionally
carries `timeControl` — the Fischer clock governing the whole match — and both
players' remaining time then rides every decision query. See §10 for the
fields, the semantics, and the worked examples.

Between `matchStarted` and `matchEnded`, expect decision queries. After
`matchEnded` you remain registered and idle — unless you forfeited, in which
case the server closes your connection after sending `matchEnded`.

## 8. Provably-fair dice

Unseeded matches run on **verifiable dice**: the server commits to a secret dice
key before the first roll and reveals it at match end, so either player can
re-derive every roll and confirm the dice were fixed in advance and never
adapted to their play. The scheme is deliberately public — verification requires
re-implementing it — and language-neutral; unpredictability lives solely in the
secret key.

A match is in fair mode when its `matchStarted` (§7) carries `diceCommitment`
and `diceAlgorithm`. Explicit-seed matches — a reproducibility/development mode
selected by supplying a seed — carry neither and are not verifiable.

### 8.1 The algorithm

`diceAlgorithm` is `"hmac-sha256-dice-v1"`. A verifier that does not recognize
this exact string cannot check the rolls. The key is exactly 32 bytes; `BE64`
below is an 8-byte big-endian integer.

- **Key.** A 256-bit (32-byte) secret, revealed as `matchEnded.diceKey`, a
  lowercase-hex string of 64 digits.
- **Commitment.** `diceCommitment` is `SHA-256(key ‖ UTF-8(context))` as
  lowercase hex, where the **context** is the exact string
  `bg-tournament:match:<matchId>` built from the match's own id. Because the key
  length is fixed at 32 bytes the concatenation is unambiguous — no separator.
  Binding the id means a commitment for one match cannot be replayed for another.
- **Keystream.** The concatenation of `HMAC-SHA256(key, BE64(blockIndex))` for
  `blockIndex = 0, 1, 2, …`. Each block contributes 32 bytes.
- **Die (rejection sampling).** To produce one die, read the next keystream byte
  `b`: if `b ≥ 252`, reject it and read the next; otherwise the die is
  `(b mod 6) + 1`. 252 is the largest multiple of 6 that is ≤ 256, so bytes
  0–251 map uniformly onto the six faces and 252–255 are discarded — removing the
  modulo bias a naïve `b mod 6` over all 256 values would introduce.
- **Roll.** One roll is the next two accepted dice, `(die1, die2)`. Rejected
  bytes are consumed but never surface; the stream stays aligned across them.

### 8.2 Roll indexing

Each `playQuery` in fair mode carries `rollIndex`: the 0-based ordinal of the
server roll that produced its `die1`/`die2`. **Every roll the server takes
advances the index by one** — the opening roll and each re-roll on an opening
tie, and every turn's roll (including a dance turn, where no legal play exists
and you are not queried). Cube offers and responses take no roll. So the rolls
you observe on your own play queries are a **gapped subsequence** of the stream:
your opponent's rolls, your dances, and opening re-rolls all consume indices you
never see. The index is what lets you place each observed roll at its true
position in the committed stream.

Worked example (the all-zero key, from the published vectors in §8.3): its
stream begins `(4,4), (1,5), …`. The opening roll `(4,4)` is a tie at index 0,
re-rolled and never shown. Index 1 is `(1,5)` — seat One's die 1, seat Two's die
5 — so seat Two wins the opening and is queried to play `1` and `5`, at
`rollIndex` 1:

```json
{"type":"playQuery","requestId":"q-1","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered","matchLength":7,"yourScore":0,"opponentScore":0,"isCrawford":false},"die1":1,"die2":5,"rollIndex":1}
```

### 8.3 Verifying a match

Given `diceCommitment` and `diceAlgorithm` from `matchStarted`, the revealed
`diceKey` from `matchEnded`, and the `(rollIndex, die1, die2)` triples you
observed on your play queries:

1. Confirm `diceAlgorithm` is `"hmac-sha256-dice-v1"`.
2. Rebuild `context = "bg-tournament:match:" + matchId` and check that
   `SHA-256(key ‖ UTF-8(context))` equals `diceCommitment`. If not, the revealed
   key is not the one committed to — stop.
3. Re-derive the stream from the key (§8.1) up to your highest observed index.
4. For each observed `(rollIndex, die1, die2)`, confirm the derived roll at
   `rollIndex` equals `(die1, die2)`.

If every check passes, every roll you were shown came from the one committed
stream. Committed cross-language test vectors — commitment, first keystream
block, and roll sequences, including keys that exercise the rejection branch —
are published at `BgGame_Lib/spec/verifiable-dice-vectors.json`; a
re-implementation in any language must reproduce them.

### 8.4 What it proves — and what it doesn't

- **Non-adaptivity (proven).** The whole sequence is fixed by the key, and the
  key is committed before roll one, so the server cannot react to your play: a
  post-hoc change fails the commitment check.
- **Observed-roll integrity (proven, per §8.2).** Indexed verification places
  each roll you saw at its committed position, so none was substituted. Rolls you
  did not see — your opponent's, dances — are not on the wire; they are auditable
  from the server's match transcript, not by an engine over the protocol.
- **Non-selection (not proven).** In version 1 the server alone supplies the
  entropy, so nothing stops it from generating many keys and committing to a
  favorable one before the match begins. Commit-and-reveal binds the server to
  one sequence; it does not prove that sequence was not cherry-picked. Closing
  this gap needs **contributory entropy**: the server commits first, each engine
  then mixes in its own nonce, and the effective key derives from all
  contributions (e.g. via HKDF) so no single party controls the result. That
  requires engines to participate in a new required exchange, so it is a future
  protocol version, not a v1 field (§11).

## 9. Failures and forfeits

The server forfeits an engine's match when the engine:

- replies with an **illegal play** or an out-of-contract action value,
- replies with a **malformed message** (invalid JSON, wrong reply type,
  unknown `requestId`, missing fields, out-of-range values),
- sends an **unsolicited message** during a match,
- exceeds the **per-decision timeout**, in a match without a time control
  (server-configured; reference default 30 seconds per decision),
- empties its **match clock** mid-decision, in a time-control match — the
  flag falls (§10), or
- **disconnects** while in a match — even while its opponent is thinking.

The forfeiting engine's opponent wins the match. Both engines are informed via
`matchEnded` (`reason: "forfeit"`) where their connections still permit it (an
engine that disconnected or sent unparseable frames may not be reachable); the
offender's connection is then closed.
There is no sanction beyond the match: version 1 has no rating, suspension, or
retry semantics.

One benign race is tolerated: if your match ends while your reply is in
flight (your opponent forfeited while you were deciding), your late reply to
the just-abandoned query is discarded, not punished.

## 10. Timing and time controls

Every match runs exactly one of two timing regimes. Which one is fixed when
the match is started (an admin-side choice — engines are not consulted) and
announced on `matchStarted`.

### 10.1 The flat per-decision timeout (default)

Without a time control, the only timing rules are the handshake timeout (§3)
and the per-decision timeout (§9), both server-configured. There is no match
clock: `matchStarted` carries no `timeControl`, and queries carry no clock
fields.

### 10.2 Fischer match clocks

A match started with a time control announces it on `matchStarted`:

```json
{"type":"matchStarted","matchId":"m-3","opponent":"RandomBot","matchLength":7,"timeControl":{"initialSeconds":120,"incrementSeconds":8}}
```

Each player starts the match with a pool of `initialSeconds` on their clock.
The pool spans the whole match — every game, every decision. Then:

- **Debit.** Answering a decision query costs the wall-clock time the server
  measures between sending you the query and receiving your reply. **Network
  latency is on your clock**: the server's own measurement is the only
  server-authoritative option (self-reported timings would be gameable), so
  budget for your round trip.
- **Credit.** Each answered decision credits your pool `incrementSeconds` —
  the Fischer increment; play replies and cube replies alike. Unused time
  banks: the pool may grow beyond `initialSeconds`.
- **The flag.** If your pool empties while your decision is pending, you
  forfeit the match (§9). Under a time control the flat per-decision timeout
  does not apply — your remaining pool is the only limit on any single
  decision, so a large pool may be spent on one long think.
- **What costs nothing.** Time outside your own decisions — your opponent's
  thinking, notifications, the gaps between games — is never on your clock.

Every decision query carries both clocks, in seconds (fractions allowed), as
of the moment the query was issued — the pending decision's own cost is not
yet debited. As everywhere, the fields are in your frame: `yourTimeRemainingSeconds`
is the queried player's own pool.

```json
{"type":"playQuery","requestId":"q-2","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeValue":1,"cubeOwner":"centered","matchLength":7,"yourScore":0,"opponentScore":0,"isCrawford":false},"die1":3,"die2":1,"yourTimeRemainingSeconds":95.5,"opponentTimeRemainingSeconds":120}
```

A per-decision cap needs no separate mode: `initialSeconds` =
`incrementSeconds` = *C* guarantees at least *C* seconds for every decision,
with Fischer banking of whatever you do not use.

All of this is additive under §2's unknown-field rule — an engine that ignores
the fields plays identically, until its flag falls. Reading them is optional;
the clock runs server-side regardless.

## 11. Deliberate version-1 gaps

Recorded so they read as decisions, not oversights — candidates for future
versions:

- **No `aborted` reason.** A match stopped by the server itself (shutdown,
  internal error) ends silently on the wire; `matchEnded` never lies about
  the reason. Server-side records carry the truth.
- **No per-game notifications.** Engines learn game boundaries implicitly
  (a fresh opening position in the next query); only match start/end are
  announced.
- **Forfeit scores.** On a forfeit the reported points may be `0`–`0`: the
  match is decided by the forfeit itself, and version-1 servers may not
  retain mid-match scores (§9).
- **Clock arbitration.** In a time-control match (§10) the server's time
  measurement is authoritative and unilateral: the remaining-time fields are
  informational, and version 1 gives an engine no way to audit or dispute a
  debit (no per-decision timing log on the wire).
- **Dice non-selection.** Fair-mode dice (§8) prove the server did not adapt
  the rolls, but not that it did not cherry-pick a favorable committed
  sequence — v1 uses server-only entropy. Contributory nonces close this in a
  future version (§8.4).
- **Bearer-token registration.** The `engineKey` (§3.1) is a static bearer
  secret sent in the clear at the protocol level; `wss://` termination is the
  transit answer. Keypair challenge–response (the server proves possession
  without the secret crossing the wire) is the recorded upgrade path for a
  future version.

## 12. Version history

- **1** — initial protocol: handshake, three decision queries, match
  lifecycle notifications, forfeit semantics.
  - *Minor addition (fair dice):* optional, ignorable fields —
    `matchStarted.diceCommitment` / `diceAlgorithm`, `matchEnded.diceKey`, and
    `playQuery.rollIndex` (§8). Additive under §2's unknown-field rule; an
    unaware engine ignores them and plays identically, so this stays version 1.
  - *Minor addition (time controls):* optional, ignorable fields —
    `matchStarted.timeControl` and `yourTimeRemainingSeconds` /
    `opponentTimeRemainingSeconds` on every decision query (§10). Additive
    under §2's unknown-field rule; an unaware engine plays identically —
    though in a time-control match its flag can still fall, which is a
    server-side rule, not a wire change.
  - *Minor addition (registration):* the optional `hello.engineKey` field and
    the server-side Open/Registered engine policy (§3.1). Additive under §2's
    unknown-field rule; a keyless hello is byte-identical to before — though
    an enforcing server rejects it, which is a server-side policy, not a wire
    change.
