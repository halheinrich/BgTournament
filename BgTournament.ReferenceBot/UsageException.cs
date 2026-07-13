namespace BgTournament.ReferenceBot;

/// <summary>
/// A command-line usage problem: a missing required argument, an unknown flag,
/// or a malformed value. Its message names the specific problem; the caller
/// pairs it with <see cref="ReferenceBot.UsageText"/> and exits
/// <see cref="ExitCode.UsageError"/>.
/// </summary>
internal sealed class UsageException : Exception
{
    /// <summary>Create a usage error describing the specific problem.</summary>
    public UsageException(string message)
        : base(message)
    {
    }
}
