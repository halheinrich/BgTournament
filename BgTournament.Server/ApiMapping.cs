using BgGame_Lib;
using BgTournament.Api;
using BgTournament.Core;
using ApiDecisionKind = BgTournament.Api.DecisionKind;
using ApiForfeitCause = BgTournament.Api.ForfeitCause;
using ApiResultKind = BgTournament.Api.GameResultKind;
using SubstrateResultKind = BgGame_Lib.GameResultKind;

namespace BgTournament.Server;

/// <summary>
/// The only home of server-internal → admin-API projections (the admin
/// surface's counterpart to <c>WireMapping</c> on the wire side). The
/// contracts assembly stays free of server internals, and the server never
/// serializes an internal or substrate type directly — everything crossing
/// the HTTP boundary passes through here (the shape walkers,
/// <c>ReplayProjection</c> and <c>AuditProjection</c>, compose these
/// per-field and per-enum correspondences).
/// </summary>
internal static class ApiMapping
{
    /// <summary>Project a connected engine session onto its admin summary.</summary>
    public static EngineSummary ToSummary(this EngineSession session) =>
        new(session.Name, session.Version, session.Author, session.InMatch);

    /// <summary>Project a hosted-match record onto its admin summary.</summary>
    public static MatchSummary ToSummary(this MatchRecord record) =>
        new(
            record.MatchId,
            record.EngineOne,
            record.EngineTwo,
            record.MatchLength,
            record.MaxGames,
            record.Seed,
            record.TimeControl,
            record.Status,
            record.Winner,
            record.SeatOneScore,
            record.SeatTwoScore,
            record.ForfeitedBy,
            record.ForfeitCause is { } cause ? ToApiForfeitCause(cause) : null,
            record.Detail,
            record.StartedAtUtc,
            record.EndedAtUtc);

    /// <summary>
    /// Project a roster record onto its admin entry. Deliberately
    /// credential-free: no hash material ever crosses the HTTP boundary.
    /// </summary>
    public static RosterEntry ToEntry(this RosterEngineRecord engine) =>
        new(
            engine.Name,
            engine.Attestation,
            engine.Active,
            engine.RegisteredAtUtc,
            engine.RegisteredBy,
            engine.KeyRotatedAtUtc,
            engine.DeactivatedAtUtc);

    /// <summary>Project a domain standings row onto its admin shape.</summary>
    public static StandingEntry ToStandingEntry(this StandingsRow row) =>
        new(row.Rank, row.Participant, row.Wins, row.Losses, row.SonnebornBerger);

    /// <summary>Seat-key a substrate seat (One = the record's <c>engineOne</c>).</summary>
    public static Seat ToSeat(MatchSeat seat) =>
        seat == MatchSeat.One ? Seat.One : Seat.Two;

    /// <summary>Project a substrate win kind onto the admin vocabulary.</summary>
    public static ApiResultKind ToResultKind(SubstrateResultKind kind) => kind switch
    {
        SubstrateResultKind.WinSingle => ApiResultKind.Single,
        SubstrateResultKind.WinGammon => ApiResultKind.Gammon,
        SubstrateResultKind.WinBackgammon => ApiResultKind.Backgammon,
        _ => throw new InvalidOperationException($"Unhandled GameResultKind value: {kind}."),
    };

    /// <summary>Project the server's forfeit taxonomy onto the admin vocabulary.</summary>
    public static ApiForfeitCause ToApiForfeitCause(ForfeitCause cause) => cause switch
    {
        ForfeitCause.ContractViolation => ApiForfeitCause.ContractViolation,
        ForfeitCause.Timeout => ApiForfeitCause.Timeout,
        ForfeitCause.FlagFall => ApiForfeitCause.FlagFall,
        ForfeitCause.Disconnect => ApiForfeitCause.Disconnect,
        ForfeitCause.NeverConnected => ApiForfeitCause.NeverConnected,
        _ => throw new InvalidOperationException($"Unhandled ForfeitCause value: {cause}."),
    };

    /// <summary>Project the server's decision vocabulary onto the admin one.</summary>
    public static ApiDecisionKind ToApiDecisionKind(DecisionKind kind) => kind switch
    {
        DecisionKind.Play => ApiDecisionKind.Play,
        DecisionKind.CubeOffer => ApiDecisionKind.CubeOffer,
        DecisionKind.CubeResponse => ApiDecisionKind.CubeResponse,
        _ => throw new InvalidOperationException($"Unhandled DecisionKind value: {kind}."),
    };
}
