using System.Runtime.CompilerServices;
using System.Text;
using BgTournament.Api;
using BgTournament.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TournamentOptions>(builder.Configuration.GetSection("Tournament"));

// The one timestamp source for clock logic (never ambient DateTime/Stopwatch),
// so match clocks are deterministic under test.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EngineRegistry>();
builder.Services.AddSingleton<MatchService>();
builder.Services.AddSingleton<TournamentService>();

var app = builder.Build();

app.UseWebSockets();

// The engine wire endpoint (PROTOCOL.md).
app.Map("/engine", EngineSocketEndpoint.HandleAsync);

// The admin HTTP surface. Its shapes are the BgTournament.Api contracts —
// self-describing (string enums are pinned per member), so no serializer
// configuration is needed here beyond ASP.NET Core's Web defaults.
app.MapGet("/engines", (EngineRegistry registry) =>
    Results.Ok(registry.Snapshot().Select(ApiMapping.ToSummary)));

app.MapPost("/matches", (StartMatchRequest request, MatchService matches) =>
{
    var (record, error, detail) = matches.StartMatch(
        request.EngineOne, request.EngineTwo, request.MatchLength, request.Seed, request.MaxGames,
        request.TimeControl);
    return error switch
    {
        StartMatchError.None => Results.Ok(record!.ToSummary()),
        StartMatchError.UnknownEngine => Results.NotFound(new ErrorResponse(detail!)),
        StartMatchError.EngineBusy or StartMatchError.SameEngine => Results.Conflict(new ErrorResponse(detail!)),
        _ => Results.BadRequest(new ErrorResponse(detail!)),
    };
});

app.MapGet("/matches", (MatchService matches) =>
    Results.Ok(matches.ListRecords().Select(ApiMapping.ToSummary)));

app.MapGet("/matches/{matchId}", (string matchId, MatchService matches) =>
    matches.TryGetRecord(matchId, out var record)
        ? Results.Ok(record.ToSummary())
        : Results.NotFound());

// Replay: a terminal match's per-game transcripts, every position re-expressed
// in seat One's frame so viewers keep a stable orientation. A completed match
// serves its full games; a forfeited/aborted/faulted one serves the games that
// finished before the break (partial — the response's status says so).
app.MapGet("/matches/{matchId}/games", (string matchId, MatchService matches) =>
{
    if (!matches.TryGetRecord(matchId, out var record))
    {
        return Results.NotFound();
    }

    // A running match has no settled games yet — the live feed is its surface.
    return record.Status == MatchStatus.Running
        ? Results.Conflict(new ErrorResponse(
            $"Match '{matchId}' is still running; watch it at /matches/{matchId}/live, "
                + "or read its games once it ends."))
        : Results.Ok(record.ToGamesResponse());
});

// Export: a terminal match rendered as Jellyfish .MAT text, served as a
// download. Same terminal-only gate as replay — a running match is refused,
// pointed at the live feed. Every terminal match exports: completed and money
// sessions carry their per-game results; a forfeited/aborted/faulted one carries
// the games that finished, the trailing in-flight game, and a winner/reason
// comment (MatExportProjection picks the factory). The exporter's bytes are
// LF-only with no trailing whitespace — served verbatim (no BOM, no re-framing).
app.MapGet("/matches/{matchId}/export.mat", IResult (string matchId, MatchService matches) =>
{
    if (!matches.TryGetRecord(matchId, out var record))
    {
        return Results.NotFound();
    }

    if (record.Status == MatchStatus.Running)
    {
        return Results.Conflict(new ErrorResponse(
            $"Match '{matchId}' is still running; watch it at /matches/{matchId}/live, "
                + "or export it once it ends."));
    }

    byte[] mat = Encoding.UTF8.GetBytes(record.ToMatText());
    return Results.File(mat, "text/plain; charset=utf-8", $"match_{matchId}.mat");
});

// Live spectating: a Server-Sent Events feed of one match as it plays. Each
// subscriber gets a join-in-progress snapshot, then per-move increments, then
// a terminal event, then close. Payloads are the same replay contracts the
// settled-replay endpoint serves, projected identically.
app.MapGet("/matches/{matchId}/live", IResult (string matchId, MatchService matches, CancellationToken ct) =>
{
    if (!matches.TryGetRecord(matchId, out var record))
    {
        return Results.NotFound();
    }

    // Subscribe before the stream begins: the snapshot is captured and the
    // subscriber registered atomically, so no event falls in the gap.
    return TypedResults.ServerSentEvents(StreamLive(record.Live.Subscribe(), ct));
});

// Round-robin tournaments: server-side orchestration over the same match
// host — invisible on the wire (engines just see per-match lifecycles).
app.MapPost("/tournaments", (StartTournamentRequest request, TournamentService tournaments) =>
{
    var (record, error, detail) = tournaments.StartTournament(
        request.Participants, request.MatchLength, request.MatchesPerPairing, request.Seed,
        request.TimeControl);
    return error switch
    {
        StartTournamentError.None => Results.Ok(tournaments.Summarize(record!)),
        StartTournamentError.UnknownEngine => Results.NotFound(new ErrorResponse(detail!)),
        StartTournamentError.EngineBusy => Results.Conflict(new ErrorResponse(detail!)),
        _ => Results.BadRequest(new ErrorResponse(detail!)),
    };
});

app.MapGet("/tournaments", (TournamentService tournaments) =>
    Results.Ok(tournaments.ListRecords().Select(tournaments.Summarize)));

app.MapGet("/tournaments/{tournamentId}", (string tournamentId, TournamentService tournaments) =>
    tournaments.TryGetSummary(tournamentId, out var summary)
        ? Results.Ok(summary)
        : Results.NotFound());

app.Run();

// Drain one subscriber's ordered event stream to the SSE response, detaching
// it when the stream ends — whether the match went terminal (the channel
// completes) or the client disconnected (the request token cancels).
static async IAsyncEnumerable<LiveMatchEvent> StreamLive(
    LiveSubscription subscription, [EnumeratorCancellation] CancellationToken cancellationToken)
{
    try
    {
        await foreach (var liveEvent in subscription.Reader.ReadAllAsync(cancellationToken))
        {
            yield return liveEvent;
        }
    }
    finally
    {
        subscription.Unsubscribe();
    }
}
