using System;

namespace LunaServerGui.Models;

/// <summary>
/// Outcome of one client's handshake attempt.
/// </summary>
public enum ConnectionOutcome
{
    /// <summary>Handshake succeeded; client is now connected.</summary>
    Accepted,
    /// <summary>Handshake refused by the server (banned, server-full, invalid playername, etc.).</summary>
    Rejected,
}

/// <summary>
/// One parsed connection event sourced from the server's stdout. The
/// timestamp is captured by the GUI when the line arrives (the server's
/// log line carries HH:mm:ss only, no date — using GUI-side UtcNow avoids
/// the midnight-rollover edge case).
///
/// Mod-mismatch rejections are NOT represented here: the server has zero
/// visibility into them (validation runs client-side in
/// LmpClient/Systems/ModFile/ModFileHandler.cs and the client self-
/// disconnects without ever telling the server why). The Connections
/// view surfaces this caveat in a header banner.
/// </summary>
public sealed record ConnectionEvent(
    DateTime Timestamp,
    ConnectionOutcome Outcome,
    string PlayerName,
    string UniqueIdentifier,
    string? Reason,
    string? LmpVersion,
    string? KspVersion)
{
    /// <summary>
    /// Short visible glyph for the outcome — Avalonia renders the string
    /// as-is, no extra converters needed.
    /// </summary>
    public string OutcomeGlyph => Outcome switch
    {
        ConnectionOutcome.Accepted => "✓",
        ConnectionOutcome.Rejected => "✗",
        _ => "?",
    };

    /// <summary>Local-time formatted timestamp for the row's first column.</summary>
    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>
    /// Compact details column: for rejections, the reason; for accepts,
    /// the LMP+KSP version string. Operator sees one column carrying
    /// whichever side's information is relevant to the outcome.
    /// </summary>
    public string Details => Outcome switch
    {
        ConnectionOutcome.Rejected => Reason ?? "(no reason captured)",
        ConnectionOutcome.Accepted => $"LMP {LmpVersion ?? "?"}, KSP {KspVersion ?? "?"}",
        _ => string.Empty,
    };

    /// <summary>
    /// First 8 chars of the UniqueIdentifier, for the cramped UID column.
    /// Operators can hover (ToolTip in the View) to see the full value.
    /// </summary>
    public string UidShort => UniqueIdentifier.Length > 8
        ? UniqueIdentifier.Substring(0, 8) + "…"
        : UniqueIdentifier;
}
