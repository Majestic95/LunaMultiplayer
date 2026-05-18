using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LunaServerGui.Models;

namespace LunaServerGui.Services;

public sealed class ServerFolderService
{
    private const string WindowsEntrypointName = "Server.exe";
    private const string LinuxEntrypointName = "Server";
    private const string DotnetEntrypointName = "Server.dll";

    public ServerFolderValidation Validate(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return new ServerFolderValidation(
                Path: string.Empty,
                Status: FolderValidationStatus.Invalid,
                Entrypoint: null,
                Findings: new[] { new ValidationFinding(FindingSeverity.Error, "No folder selected.") });
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rawPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return new ServerFolderValidation(
                Path: rawPath,
                Status: FolderValidationStatus.Invalid,
                Entrypoint: null,
                Findings: new[] { new ValidationFinding(FindingSeverity.Error, $"Path is not a valid filesystem path: {ex.Message}") });
        }

        if (!Directory.Exists(fullPath))
        {
            // Distinguish "this is a file" from "this doesn't exist" — operators
            // sometimes browse to Server.exe directly and need the hint.
            var message = File.Exists(fullPath)
                ? $"Path is a file, not a folder. Try selecting the parent directory: {Path.GetDirectoryName(fullPath)}"
                : $"Folder does not exist: {fullPath}";
            return new ServerFolderValidation(
                Path: fullPath,
                Status: FolderValidationStatus.Invalid,
                Entrypoint: null,
                Findings: new[] { new ValidationFinding(FindingSeverity.Error, message) });
        }

        var findings = new List<ValidationFinding>();
        var entrypoint = DiscoverEntrypoint(fullPath, findings);
        AddInformationalFolderFindings(fullPath, findings);

        // Sort findings so errors appear first, then warnings, then info — the
        // UI can render the list verbatim and the operator scans top-down.
        var ordered = findings
            .OrderBy(f => f.Severity)
            .ToList();

        var status = entrypoint is not null ? FolderValidationStatus.Valid : FolderValidationStatus.Invalid;
        return new ServerFolderValidation(fullPath, status, entrypoint, ordered);
    }

    private static ServerEntrypoint? DiscoverEntrypoint(string folder, List<ValidationFinding> findings)
    {
        var nativeName = OperatingSystem.IsWindows() ? WindowsEntrypointName : LinuxEntrypointName;
        var nativeCandidate = Path.Combine(folder, nativeName);
        if (File.Exists(nativeCandidate))
        {
            findings.Add(new ValidationFinding(FindingSeverity.Info, $"Found native entrypoint: {nativeName}"));
            return new ServerEntrypoint(
                Kind: EntrypointKind.NativeExecutable,
                ExecutablePath: nativeCandidate,
                Arguments: Array.Empty<string>(),
                WorkingDirectory: folder);
        }

        var dotnetCandidate = Path.Combine(folder, DotnetEntrypointName);
        if (File.Exists(dotnetCandidate))
        {
            findings.Add(new ValidationFinding(FindingSeverity.Info, $"Found framework-dependent entrypoint: {DotnetEntrypointName} (launches via `dotnet`)"));
            return new ServerEntrypoint(
                Kind: EntrypointKind.DotnetDll,
                ExecutablePath: "dotnet",
                Arguments: new[] { dotnetCandidate },
                WorkingDirectory: folder);
        }

        findings.Add(new ValidationFinding(FindingSeverity.Error,
            $"No server entrypoint found in {folder}. Expected {nativeName} or {DotnetEntrypointName} at the folder root."));
        return null;
    }

    private static void AddInformationalFolderFindings(string folder, List<ValidationFinding> findings)
    {
        foreach (var sub in new[] { "Config", "Universe", "Logs" })
        {
            var path = Path.Combine(folder, sub);
            findings.Add(Directory.Exists(path)
                ? new ValidationFinding(FindingSeverity.Info, $"{sub}/ exists.")
                : new ValidationFinding(FindingSeverity.Info, $"{sub}/ not yet created (will be on first run)."));
        }
    }
}
