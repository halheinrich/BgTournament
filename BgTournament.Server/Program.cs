using System.Text.Json.Serialization;
using BgTournament.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TournamentOptions>(builder.Configuration.GetSection("Tournament"));
builder.Services.AddSingleton<EngineRegistry>();
builder.Services.AddSingleton<MatchService>();
builder.Services.AddSingleton<TournamentService>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

var app = builder.Build();

app.UseWebSockets();

// The engine wire endpoint (PROTOCOL.md).
app.Map("/engine", EngineSocketEndpoint.HandleAsync);

// Minimal admin surface (v1): list engines, start a match, read a match.
app.MapGet("/engines", (EngineRegistry registry) =>
    Results.Ok(registry.Snapshot().Select(EngineSummary.From)));

app.MapPost("/matches", (StartMatchRequest request, MatchService matches) =>
{
    var (record, error, detail) = matches.StartMatch(
        request.EngineOne, request.EngineTwo, request.MatchLength, request.Seed, request.MaxGames);
    return error switch
    {
        StartMatchError.None => Results.Ok(MatchSummary.From(record!)),
        StartMatchError.UnknownEngine => Results.NotFound(new { error = detail }),
        StartMatchError.EngineBusy or StartMatchError.SameEngine => Results.Conflict(new { error = detail }),
        _ => Results.BadRequest(new { error = detail }),
    };
});

app.MapGet("/matches/{matchId}", (string matchId, MatchService matches) =>
    matches.TryGetRecord(matchId, out var record)
        ? Results.Ok(MatchSummary.From(record))
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
        StartTournamentError.UnknownEngine => Results.NotFound(new { error = detail }),
        StartTournamentError.EngineBusy => Results.Conflict(new { error = detail }),
        _ => Results.BadRequest(new { error = detail }),
    };
});

app.MapGet("/tournaments/{tournamentId}", (string tournamentId, TournamentService tournaments) =>
    tournaments.TryGetSummary(tournamentId, out var summary)
        ? Results.Ok(summary)
        : Results.NotFound());

app.Run();
