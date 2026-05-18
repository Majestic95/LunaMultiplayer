using System;
using System.Text.RegularExpressions;
using LunaServerGui.Models;

namespace LunaServerGui.Services;

/// <summary>
/// Parses server-stdout log lines into <see cref="ConnectionEvent"/>s.
/// Stateless + thread-safe (callable from the OutputLineReceived
/// background thread).
///
/// The server's line format comes from <c>LmpCommon/BaseLogger.cs:22</c>:
///   <c>[HH:mm:ss][type]: message</c>
/// where type is "LMP" / "Debug" / "Warning" / etc., and the message
/// for handshake events is one of these two patterns from
/// <c>Server/System/HandshakeSystem.cs:28</c> and :50:
///   "Client {name} ({uid}) failed to handshake: {reason}. Disconnecting"
///   "Client {name} ({uid}) handshake successful, LMP Version: {lmp}, KSP Version: {ksp}"
///
/// Mod-mismatch rejections are NOT logged server-side (validation runs
/// in the client's <c>ModFileHandler.cs</c>). This parser cannot see
/// them and the Connections view surfaces that caveat to the operator.
/// </summary>
public static class ServerLogParser
{
    // The bracketed prefix is tolerant: we don't pin the time format
    // strictly because the server may evolve it (it currently uses
    // HH:mm:ss UTC). The type slot allows any non-bracket characters so
    // we match any log level.
    //
    // The name group is GREEDY (`.+`, not `.+?`) so backtracking anchors
    // on the RIGHTMOST `(uid)` parens. The rejection log line is emitted
    // in HandshakeSystem.cs BEFORE the playername-character-validation
    // chain has fully run (CheckServerFull / CheckUsernameLength /
    // CheckPlayerIsAlreadyConnected / CheckUsernameIsReserved /
    // CheckPlayerIsBanned all short-circuit before CheckUsernameCharacters
    // would have rejected parens). A malicious/odd client sending
    // PlayerName="Evil) fake" would log
    // `Client Evil) fake (real-uid) failed to handshake: ...` —
    // with the non-greedy match the parser would attribute the rejection
    // to UID="fake" instead of the real one. Greedy + backtracking
    // anchors on " (real-uid) failed to handshake:" (review SHOULD FIX #3).
    private static readonly Regex RejectionRegex = new(
        @"^\[[^\]]*\]\[[^\]]+\]:\s+Client\s+(?<name>.+)\s+\((?<uid>[^)]+)\)\s+failed to handshake:\s+(?<reason>.+?)\.\s+Disconnecting\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AcceptanceRegex = new(
        @"^\[[^\]]*\]\[[^\]]+\]:\s+Client\s+(?<name>.+)\s+\((?<uid>[^)]+)\)\s+handshake successful,\s+LMP Version:\s+(?<lmp>[^,]+),\s+KSP Version:\s+(?<ksp>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Try to parse one stdout line into a <see cref="ConnectionEvent"/>.
    /// Returns false for any line that isn't a recognised handshake
    /// event — the vast majority of server lines (vessel sync, lock
    /// activity, etc.) fall through cheaply on a single regex test.
    /// </summary>
    public static bool TryParse(string line, DateTime arrivedUtc, out ConnectionEvent? evt)
    {
        evt = null;
        if (string.IsNullOrEmpty(line)) return false;

        var reject = RejectionRegex.Match(line);
        if (reject.Success)
        {
            evt = new ConnectionEvent(
                Timestamp: arrivedUtc,
                Outcome: ConnectionOutcome.Rejected,
                PlayerName: reject.Groups["name"].Value,
                UniqueIdentifier: reject.Groups["uid"].Value,
                Reason: reject.Groups["reason"].Value,
                LmpVersion: null,
                KspVersion: null);
            return true;
        }

        var accept = AcceptanceRegex.Match(line);
        if (accept.Success)
        {
            evt = new ConnectionEvent(
                Timestamp: arrivedUtc,
                Outcome: ConnectionOutcome.Accepted,
                PlayerName: accept.Groups["name"].Value,
                UniqueIdentifier: accept.Groups["uid"].Value,
                Reason: null,
                LmpVersion: accept.Groups["lmp"].Value,
                KspVersion: accept.Groups["ksp"].Value);
            return true;
        }

        return false;
    }
}
