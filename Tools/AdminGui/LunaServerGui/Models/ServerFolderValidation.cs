using System.Collections.Generic;

namespace LunaServerGui.Models;

public enum FolderValidationStatus
{
    Valid,
    Invalid,
}

public enum FindingSeverity
{
    Error,
    Warning,
    Info,
}

public sealed record ValidationFinding(FindingSeverity Severity, string Message);

public sealed record ServerFolderValidation(
    string Path,
    FolderValidationStatus Status,
    ServerEntrypoint? Entrypoint,
    IReadOnlyList<ValidationFinding> Findings)
{
    public bool CanLaunch => Status == FolderValidationStatus.Valid && Entrypoint is not null;
}
