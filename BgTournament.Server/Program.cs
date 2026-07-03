using BgTournament.Api;
using BgTournament.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TournamentOptions>(builder.Configuration.GetSection("Tournament"));
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
        request.EngineOne, request.EngineTwo, request.MatchLength, request.Seed, request.MaxGames);
    return error switch
    {
        StartMatchError.None => Results.Ok(record!.ToSummary()),
        StartMatchError.UnknownEngine => Results.NotFound(new ErrorResponse(detail!)),
        StartMatchError.EngineBusy or StartMatchError.SameEngine => Results.Conflict(new ErrorResponse(detail!)),
        _ => Results.BadRequest(new ErrorResponse(detail!)),
    };
});

app.MapGet("/matches/{matchId}", (string matchId, MatchService matches) =>
    matches.TryGetRecord(matchId, out var record)
        ? Results.Ok(record.ToSummary())
        : Results.NotFound());

// Round-robin tournaments: server-side orchestration over the same match
// host — invisible on the wire (engines just see per-match lifecycles).
app.MapPost("/tournaments", (StartTournamentRequest request, TournamentService tournaments) =>
{
    var (record, error, detail) = tournaments.StartTournament(
        request.Participants, request.MatchLength, request.MatchesPerPairing, request.Seed);
    return error switch
    {
        StartTournamentError.None => Results.Ok(tournaments.Summarize(record!)),
        StartTournamentError.UnknownEngine => Results.NotFound(new ErrorResponse(detail!)),
        StartTournamentError.EngineBusy => Results.Conflict(new ErrorResponse(detail!)),
        _ => Results.BadRequest(new ErrorResponse(detail!)),
    };
});

app.MapGet("/tournaments/{tournamentId}", (string tournamentId, TournamentService tournaments) =>
    tournaments.TryGetSummary(tournamentId, out var summary)
        ? Results.Ok(summary)
        : Results.NotFound());

app.Run();
