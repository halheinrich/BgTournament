using BgGame_Lib;

namespace BgTournament.Server;

/// <summary>
/// Fans one match run's observer callbacks out to several consumers in order
/// (the live feed and the journal ride the same flow). Pure forwarding: each
/// consumer owns its own containment — <c>LiveMatch</c> and
/// <c>MatchJournal</c> are both non-throwing by construction, so this adds no
/// policy of its own.
/// </summary>
internal sealed class CompositeMatchObserver : IMatchObserver
{
    private readonly IMatchObserver[] _observers;

    public CompositeMatchObserver(params IMatchObserver[] observers)
    {
        _observers = observers;
    }

    /// <inheritdoc/>
    public void OnGameStarted(GameStartContext context)
    {
        foreach (var observer in _observers)
        {
            observer.OnGameStarted(context);
        }
    }

    /// <inheritdoc/>
    public void OnEntryRecorded(TranscriptEntry entry)
    {
        foreach (var observer in _observers)
        {
            observer.OnEntryRecorded(entry);
        }
    }

    /// <inheritdoc/>
    public void OnGameEnded(int gameNumber, GameRecord record)
    {
        foreach (var observer in _observers)
        {
            observer.OnGameEnded(gameNumber, record);
        }
    }

    /// <inheritdoc/>
    public void OnMatchEnded(MatchResult result)
    {
        foreach (var observer in _observers)
        {
            observer.OnMatchEnded(result);
        }
    }
}
