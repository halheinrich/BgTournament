namespace BgTournament.Api;

/// <summary>
/// The error body accompanying every non-success admin response — the typed
/// counterpart of the HTTP status code, so a consumer can surface <em>why</em>
/// a request was refused without parsing ad-hoc JSON.
/// </summary>
/// <param name="Error">Human-readable reason the request was refused.</param>
public sealed record ErrorResponse(string Error);
