using LmpCommon;

namespace Server.Web.Structures
{
    /// <summary>
    /// JSON payload for <c>GET /fork</c>. Exposes the fork name, the wire
    /// protocol version, and the list of fork-applied fix IDs so operators can
    /// identify what code is actually running without pulling the server log.
    /// Mirrors the boot banner emitted by <see cref="MainServer.Main"/>.
    /// </summary>
    public class ForkInformation
    {
        public string ForkName { get; } = ForkBuildInfo.ForkName;

        public string ProtocolVersion { get; } = LmpVersioning.CurrentVersion.ToString();

        // Defensive copy — retro-review M2. ForkBuildInfo.ActiveFixes is the single source
        // of truth and must not be aliased through a JSON payload property where a serializer
        // or test could write back into the registry.
        public string[] ActiveFixes { get; } = (string[])ForkBuildInfo.ActiveFixes.Clone();
    }
}
