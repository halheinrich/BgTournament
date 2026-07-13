# Connecting an engine to a BgTournament arena

This is the on-ramp: how to get an engine registered, connected, and answering
queries. The arena is **protocol-first** — any engine, in any language, competes
by speaking the WebSocket + JSON contract in [`PROTOCOL.md`](PROTOCOL.md) over a
single `/engine` endpoint. That document is the single source of truth for wire
behavior; this walkthrough links into it rather than restating it, and where the
two ever disagree, `PROTOCOL.md` wins.

If you are writing a .NET engine, the `BgTournament.EngineClient` SDK does the
socket and framing work for you (step 4). If you are writing in any other
language, you implement `PROTOCOL.md` directly (step 6) — the SDK is a
convenience, not the contract.

## What you'll need

- **A running server.** The reference server exposes the engine wire at
  `/engine`. Its default development URLs are `http://localhost:5251` (so
  `ws://localhost:5251/engine`) and `https://localhost:7251` (so
  `wss://localhost:7251/engine`). Untrusted networks should use `wss://` — TLS
  is the transit answer for the bearer key (PROTOCOL.md §3.1, §11).
- **Your engine**, or the bundled reference bot (step 5) to prove the path.

## 1. Register (only under the Registered policy)

A server runs one of two engine policies (PROTOCOL.md §3.1):

- **Open** — the default; any engine may connect. **Skip to step 2.**
- **Registered** — only engines on the server's roster may connect, each
  presenting its issued key.

Registration is a deliberate **administrative act**, never self-serve: an
operator records your engine and its provenance declaration on the admin surface
and hands you the key out-of-band. The operator's call looks like:

```sh
curl -X POST http://localhost:5251/roster \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: <admin-key>" \
  -d '{
        "name": "MyBot",
        "attestation": {
          "authors": ["Jane Doe"],
          "origin": "original code; public techniques only",
          "derivedFrom": null
        }
      }'
```

- `name` is your **roster identity** — the exact name your engine must claim in
  its handshake (step 2). It is unique on the roster, forever.
- `attestation` is the originality declaration (`authors`, a free-form `origin`,
  and `derivedFrom` for a fork/fine-tune/port, or `null` for original work). It
  is stored **as declared** — see [`RULES.md`](RULES.md) for the originality
  policy it commits you to.
- `X-Api-Key` is required only when the server has configured admin keys; a
  development server serves the admin surface anonymously.

The response carries your **engine key exactly once**. The server keeps only a
salted hash — store the key like the secret it is. A lost key is recovered by
rotation (`POST /roster/{name}/rotate`), never by retrieval.

## 2. Connect and handshake

Open a WebSocket to `/engine` and send your `hello`; the server answers
`welcome` (you are in) or `rejected` (it then closes the connection). On a
Registered server, include your key as `engineKey`, and make `engineName` equal
your registered name. The exact `hello` / `welcome` / `rejected` frames, the
version rule, and the registration field are in **PROTOCOL.md §3 and §3.1** —
this document does not restate the wire.

After `welcome` you stay connected and idle until an operator seats you in a
match (the server owns pairing; a match is started by naming two connected
engines). You may play any number of matches over one connection.

## 3. Answer decision queries

Once you are in a match the server drives it and owns everything authoritative —
the dice (seeded and auditable), the rules, the state, and the refereeing
(PROTOCOL.md, introduction). You answer exactly three kinds of query, each with
its state **already expressed in your own frame** (PROTOCOL.md §4–§5), so you
never ask "which side am I this time?":

- **`playQuery`** — choose your play for the given dice (PROTOCOL.md §6). Reply
  with `{from, to}` hops in your frame; an empty `moves` array means *no legal
  play*. Hits are not encoded — the server derives them.
- **`cubeOfferQuery`** — double or not (PROTOCOL.md §6).
- **`cubeResponseQuery`** — take or pass a double (PROTOCOL.md §4, §6).

An illegal or malformed reply, a disconnect, or (under a time control) an
emptied clock forfeits the match — the forfeit taxonomy is PROTOCOL.md §9 and
[`RULES.md`](RULES.md). When the server runs a match on **provably-fair dice**,
you can verify every roll you were shown against the revealed key; the recipe is
PROTOCOL.md §8, and the SDK does it for you (steps 4–5).

## 4. .NET quickstart (the SDK)

`BgTournament.EngineClient` turns any in-proc agent pair into a remote engine —
you write no socket, framing, or perspective code. Implement `BgGame_Lib`'s
`IPlayAgent` and `ICubeAgent` (the reference `RandomPlayAgent` and
`PassiveCubeAgent` in `BgTournament.EngineClient/` are worked examples), then
hand them to an `EngineClient`:

```csharp
using BgGame_Lib;
using BgTournament.EngineClient;

var identity = new EngineIdentity("MyBot", version: "1.0", author: "Jane Doe");

var client = new EngineClient(
    identity,
    new RandomPlayAgent(seed: 1),   // ← replace with your own IPlayAgent
    new PassiveCubeAgent(),         // ← replace with your own ICubeAgent
    logger: null,
    onDiceVerified: report =>       // ← optional fair-dice check (PROTOCOL.md §8)
        Console.WriteLine(report.Verified
            ? $"Dice verified: {report.ObservedRollCount} rolls."
            : $"Dice check failed: {report.Outcome} — {report.Detail}"),
    engineKey: null);               // ← your show-once key on a Registered server

await client.RunAsync(new Uri("ws://localhost:5251/engine"));
```

- Every query state arrives in your frame — map it straight onto your agent
  logic; there is no side to figure out (PROTOCOL.md §4).
- `onDiceVerified` fires after each fair-mode match with a
  `DiceVerificationReport`. A failed verdict is *reported*, never thrown — the
  policy on a suspect server is yours.
- An exception thrown by your agent drops the connection, which the server
  correctly reads as a forfeit (PROTOCOL.md §9).

## 5. Run the bundled reference bot

`BgTournament.ReferenceBot` is exactly the SDK wiring above, made runnable: the
baseline opponent, a connection smoke test, and a worked example (source under
`BgTournament.ReferenceBot/`). It plays uniformly-random legal moves and a
passive cube (never doubles, always takes).

Connect to an Open server:

```sh
dotnet run --project BgTournament.ReferenceBot -- \
  --server ws://localhost:5251/engine --name RefBot
```

Connect to a Registered server (present your key over `wss://`):

```sh
dotnet run --project BgTournament.ReferenceBot -- \
  --server wss://localhost:7251/engine --name MyBot --engine-key <key>
```

Spar two reference bots (each needs its own connection and a **distinct** seed):

```sh
dotnet run --project BgTournament.ReferenceBot -- \
  --server ws://localhost:5251/engine --name RefBot --seed 42
```

**About `--seed`.** It seeds the random play policy and defaults to `0`, so runs
are reproducible by construction. The consequence: two reference bots left at the
default make *identical* choices when handed identical dice — for varied
sparring, give each an explicit, distinct `--seed`. (The server still owns the
dice; the seed only drives which legal play the bot picks.)

When the server runs a match on fair dice (any match started without an explicit
seed), the bot prints a dice-verification line — its `onDiceVerified` hook
checking every roll it saw against the revealed key (PROTOCOL.md §8). Full flag
list: `--help`.

The process exit code names the outcome, for scripting and CI:

| Code | Meaning                                                        |
| ---- | ------------------------------------------------------------- |
| 0    | Served; the server closed the connection normally.            |
| 64   | Usage error — missing or malformed arguments.                 |
| 68   | Could not reach the server, or the connection dropped.        |
| 69   | Handshake rejected — version, name, or registration.          |
| 130  | Interrupted (Ctrl-C).                                          |

## 6. Non-.NET engines

The SDK is a .NET convenience; the arena is language-neutral. Any runtime that
speaks WebSocket and JSON can compete — implement [`PROTOCOL.md`](PROTOCOL.md)
directly. It is written in JSON terms end to end, and every example in it is
pinned byte-for-byte by the server's own tests, so it will not drift from what
the server actually sends. Fair-dice verification (PROTOCOL.md §8) is a public,
language-neutral recipe with committed cross-language test vectors, so a
re-implementation in your language can prove itself against the same numbers.

The competition rules — registration and attestation, the originality policy,
Open vs Registered engine policy, time controls, the forfeit taxonomy, fair dice,
and arbitration — are in [`RULES.md`](RULES.md).
