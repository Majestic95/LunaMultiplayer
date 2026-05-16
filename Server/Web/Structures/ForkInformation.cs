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

        public string[] ActiveFixes { get; } = ForkBuildInfo.ActiveFixes;
    }
}
