using System.Text.Json.Serialization;
using BgTournament.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TournamentOptions>(builder.Configuration.GetSection("Tournament"));
builder.Services.AddSingleton<EngineRegistry>();
builder.Services.AddSingleton<MatchService>();
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

app.Run();
