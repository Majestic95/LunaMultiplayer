using System;
using System.IO;
using System.Xml.Serialization;

namespace LunaServerGui.Services;

/// <summary>
/// Stateless XML reader for one settings file. Mirrors
/// LmpCommon/Xml/LunaXmlSerializer.ReadXmlFromPath — uses
/// System.Xml.Serialization.XmlSerializer so the round-trip matches the
/// server's serialization exactly (unknown elements are dropped, same as
/// the server's load-then-save in SettingsBase.Load).
///
/// The slice 1D-1 surface is read-only; slice 1D-2 adds the save path
/// with timestamped backup + atomic write (FileHandler.WriteAtomic
/// pattern from Stage 5.14c).
/// </summary>
public sealed class SettingsXmlService
{
    /// <summary>
    /// Read an XML file into the given Definition type. Returns null if the
    /// file does not exist. Throws on parse failure (caller wraps).
    /// </summary>
    public object? Read(Type definitionType, string path)
    {
        ArgumentNullException.ThrowIfNull(definitionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path)) return null;

        using var reader = new StreamReader(path);
        var serializer = new XmlSerializer(definitionType);
        return serializer.Deserialize(reader);
    }
}
