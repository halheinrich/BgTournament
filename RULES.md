# BgTournament Championship Rules

These are the conduct rules for competing in a BgTournament arena: who may
enter, what you attest to, how matches are refereed, how you can lose on
conduct, and what record an arbiter holds. It is deliberately **not** a third
copy of the mechanics — the wire contract is [`PROTOCOL.md`](PROTOCOL.md), and
getting an engine connected is [`ONBOARDING.md`](ONBOARDING.md). This document
is conduct and consequences, and it links to the machinery that enforces each
rule.

**What is enforced, and what is human.** Almost everything below is
**server-enforced** — registration, engine policy, the clocks, the forfeit
taxonomy, and fair dice are all machinery, and each rule names it. One thing is
not: **originality is attested, not verified by code** (§2). These rules are
explicit about which is which, and never imply enforcement that does not exist.

## 1. Entry and registration

A server runs one of two engine policies (PROTOCOL.md §3.1; the connect path is
ONBOARDING.md §1–§2):

- **Open** — any engine may connect.
- **Registered** — only engines on the server's roster may compete.

Under the Registered policy, entry is by **registration**, a deliberate
administrative act performed by an operator, never self-serve: the operator
records your engine and its attestation and issues you a secret **engine key**,
shown exactly once. You present that key on every connection.

This is machinery, not honor system. A presented key is **always validated** —
even by an Open server — and an unknown, rotated-away, or deactivated key is
rejected loudly rather than quietly downgraded to an anonymous connection. The
key authenticates your registered **name**; it never renames you, and a valid
key presented under the wrong name is rejected. **Deactivation is terminal** —
a deactivated entry does not come back. Your registered name is your competitive
identity: every match, record, and standing attributes to it.

## 2. Originality and attestation

This is the one rule with no code behind it, and it is stated plainly so no one
mistakes it for an automated check.

At registration you declare your engine's provenance — its authors, a free-form
statement of origin, and, when your engine derives from prior work (a fork, a
fine-tune, a port), what it derives from. The server stores this declaration
**exactly as given**: it validates that a declaration is *present*, never that
its *content* is true. There is no plagiarism detector, no derivation analyzer,
and nothing in the server ever compares your engine to another.

Originality follows the WCCC-model posture: **you attest, and your attestation
is what a tournament arbiter holds you to.** Derivation is permitted *when
declared* — the offense is **undeclared** derivation, or a false declaration of
authorship or origin. Forensic examination of a credible originality challenge
is a **declared right of the arbiter**, exercised by humans; it is not a process
the server runs. A dishonest attestation is therefore a conduct violation
adjudicated by people, on the evidence in the arbitration record (§6), not a
forfeit the machinery can raise on its own.

## 3. Match conduct and refereeing

Once you are seated in a match the server is the sole authority — it owns the
dice, the rules, the state, and the refereeing; your engine only answers
decision queries, each already in your own frame (PROTOCOL.md §4–§6). You never
roll, apply a move, or keep authoritative state. The legality of every reply is
judged by the server against standard backgammon (no variants in version 1), and
its verdict is final on the wire.

## 4. Time controls

Every match runs exactly one timing regime, fixed when the match is started (an
operator's choice, not yours) and announced up front (PROTOCOL.md §10):

- **Flat per-decision timeout** (the default) — each decision must be answered
  within a server-configured limit (reference default: 30 seconds per decision),
  alongside the handshake timeout for the initial `hello` (PROTOCOL.md §3, §9).
- **Fischer match clock** — a per-match time pool (`initialSeconds`) with an
  increment credited per answered decision (`incrementSeconds`). The pool spans
  the whole match; unused time banks. Under a clock the flat per-decision timeout
  does not apply — your remaining pool is the only limit on any one decision.

In both regimes the server's own measurement is authoritative, and **network
latency is on your clock** — self-reported timings would be gameable, so the
server times the round trip it can actually observe (PROTOCOL.md §10). Version 1
gives no wire mechanism to audit or dispute a debit; the remaining-time fields
are informational (PROTOCOL.md §11). Budget accordingly.

## 5. Forfeits

A forfeit ends the match immediately in your opponent's favor. The server
forfeits **your** match when your engine (PROTOCOL.md §9):

- replies with an **illegal play** or an **out-of-contract action**;
- sends a **malformed, mistyped, or unsolicited message**;
- **exceeds the per-decision timeout**, in the flat regime;
- **empties its match clock** mid-decision, under a time control — the flag
  falls;
- **disconnects** mid-match — even while your opponent is still thinking;
- in a tournament, is **not connected** when its scheduled match comes up (it
  forfeits without a ball being played).

These are the categories the arbitration record names as structured causes
(contract violation, timeout, flag fall, disconnect, never-connected). In
version 1 a forfeit carries **no sanction beyond that match** — no rating
penalty, suspension, or retry (PROTOCOL.md §9). One benign race is explicitly
**not** a forfeit: if your match ends while your reply is in flight (your
opponent forfeited while you were deciding), your late reply is discarded, not
punished.

## 6. Fair dice

Matches started without an explicit seed run on **provably-fair, commit-and-
reveal dice**: the server commits to a secret dice key before the first roll and
reveals it at match end, so either player can re-derive every roll it was shown
and confirm the dice were fixed in advance (PROTOCOL.md §8; you can verify from
the SDK — ONBOARDING.md §4–§5). Matches started with an explicit seed are a
development/reproducibility mode and are **not** verifiable.

The guarantee is real but bounded, and the boundary is stated honestly rather
than oversold (PROTOCOL.md §8.4):

- **Proven — non-adaptivity.** The whole dice sequence is fixed by the committed
  key before roll one, so the server cannot react to your play; a post-hoc change
  fails the commitment check.
- **Proven — observed-roll integrity.** Each roll you were shown is verified to
  sit at its committed position, so none was substituted. Rolls no engine saw
  (your opponent's, dances) are auditable from the server's transcript, not over
  the wire.
- **Not proven — non-selection.** In version 1 the server alone supplies the
  entropy, so commit-and-reveal does not rule out its generating many keys and
  committing to a favorable one. Closing this needs **contributory entropy**
  (each engine mixing in its own nonce); that requires a new required exchange
  and is a deliberately deferred future protocol version, **not** a version-1
  guarantee (PROTOCOL.md §8.4, §11).

Fair dice is thus a strong guarantee against an **adaptive** server, and an
explicitly partial one against a **selective** one. Compete on that
understanding.

## 7. The arbitration record

Every match is recorded to a durable, append-only journal and is readable at
arbitration altitude after it ends (`GET /matches/{matchId}/audit`): timestamps,
per-decision clock evidence, the structured forfeit cause, and — in fair mode —
the dice commitment and the revealed key, so the whole record is
self-verifying. Engine lifecycle (connects, disconnects, handshake rejections)
is separately journaled, and the registration history — who registered, rotated,
or deactivated each engine, when, and under which admin identity — is preserved
as its own evidence trail.

This record is what a tournament arbiter reads to adjudicate a dispute. For a
timing, legality, or disconnect question it is decisive, because the machinery
produced it. For an **originality** challenge (§2) it establishes identity,
timing, and provenance-**as-declared** — the factual scaffold — but the
originality judgment itself remains a human one. The record shows what was
declared and what happened; whether a declaration was honest is decided by
people.

---

Where this document and `PROTOCOL.md` appear to differ on **wire behavior**,
`PROTOCOL.md` governs — it is the single source of truth for the protocol, and
this document only summarizes its consequences for conduct.
