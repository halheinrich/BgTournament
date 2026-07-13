namespace BgTournament.ReferenceBot;

/// <summary>
/// The process exit codes the reference bot returns, so a caller (or CI, or a
/// competitor's launch script) can distinguish outcomes without parsing text.
/// Values follow the BSD <c>sysexits.h</c> conventions where one applies.
/// </summary>
internal enum ExitCode
{
    /// <summary>The session ran and the server closed the connection normally.</summary>
    Success = 0,

    /// <summary>The command-line arguments were missing or malformed (<c>EX_USAGE</c>).</summary>
    UsageError = 64,

    /// <summary>The server could not be reached, or the connection dropped abnormally (<c>EX_NOHOST</c>).</summary>
    ConnectionFailed = 68,

    /// <summary>The server rejected the handshake — version, name, or registration (<c>EX_UNAVAILABLE</c>).</summary>
    HandshakeRejected = 69,

    /// <summary>The operator interrupted the session (Ctrl-C): 128 + SIGINT.</summary>
    Canceled = 130,
}
