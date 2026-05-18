using System.Collections.Generic;

namespace LunaServerGui.Models;

public enum EntrypointKind
{
    NativeExecutable,
    DotnetDll,
}

public sealed record ServerEntrypoint(
    EntrypointKind Kind,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);
