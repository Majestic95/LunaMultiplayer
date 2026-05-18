using System;
using System.Net;

namespace LunaServerGui.SettingsCatalog.Definitions;

// Duplicated from Server/Settings/Definition/ConnectionSettingsDefinition.cs.
// MaximumTransmissionUnit's default in the server is
// NetPeerConfiguration.kDefaultMTU (Lidgren constant = 1408). The GUI does
// not reference Lidgren; the literal 1408 is hard-coded here. If Lidgren
// ever bumps that constant the server-default and GUI-default will drift —
// not load-bearing since the operator's existing XML provides the value;
// the GUI's default only matters when the file is missing.
[Serializable]
public class ConnectionSettingsDefinition
{
    [XmlComment(Value = "The address the server listens on, needs to be either [::] or 0.0.0.0. " +
                        "If set to the unspecified IPv6 address [::], the server listens for both IPv6 and IPv4")]
    public string ListenAddress { get; set; } = IPAddress.IPv6Any.ToString();

    [XmlComment(Value = "The UDP port the server listens on. You don't need to open it on your router if RegisterWithMasterServer = true. " +
                        "If you want that players can connect against your server MANUALLY you will need to open it on your router")]
    public int Port { get; set; } = 8800;

    [XmlComment(Value = "Heartbeat interval in ms. MUST be lower than the ConnectionMsTimeout value.")]
    public int HearbeatMsInterval { get; set; } = 1000;

    [XmlComment(Value = "Connection timeout in ms. If no heartbeats are received after this interval, the client is disconnected.")]
    public int ConnectionMsTimeout { get; set; } = 30000;

    [XmlComment(Value = "Tries to use UPnP to open the ports in your router")]
    public bool Upnp { get; set; } = true;

    [XmlComment(Value = "UPnP timeout in ms for trying to open the ports")]
    public int UpnpMsTimeout { get; set; } = 5000;

    [XmlComment(Value = "Maximum transmission unit (MTU) size in bytes. Min value is 1. Default value is 1408. Max value is 8192.")]
    public int MaximumTransmissionUnit { get; set; } = 1408;

    [XmlComment(Value = "Try to expand MTU size")]
    public bool AutoExpandMtu { get; set; } = false;
}
