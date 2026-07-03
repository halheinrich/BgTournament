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
won't be queried.) An illegal or unresolvable play forfeits the match; see §8.

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

Between `matchStarted` and `matchEnded`, expect decision queries. After
`matchEnded` you remain registered and idle — unless you forfeited, in which
case the server closes your connection after sending `matchEnded`.

## 8. Failures and forfeits

The server forfeits an engine's match when the engine:

- replies with an **illegal play** or an out-of-contract action value,
- replies with a **malformed message** (invalid JSON, wrong reply type,
  unknown `requestId`, missing fields, out-of-range values),
- sends an **unsolicited message** during a match,
- exceeds the **per-decision timeout** (server-configured; reference default
  30 seconds per decision), or
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

## 9. Timing

Version 1 has no chess-style clocks. The only timing rules are the handshake
timeout (§3) and the per-decision timeout (§8), both server-configured. Real
time controls are planned for a future protocol version.

## 10. Deliberate version-1 gaps

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
  retain mid-match scores (§8).
- **No time controls** beyond the two timeouts (§9).

## 11. Version history

- **1** — initial protocol: handshake, three decision queries, match
  lifecycle notifications, forfeit semantics.
