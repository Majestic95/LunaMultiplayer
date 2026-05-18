using System;

namespace LunaServerGui.SettingsCatalog;

// Duplicated from LmpCommon/Xml/XmlCommentAttribute.cs. The GUI's catalog
// reads this attribute off duplicated Definition POCOs to surface inline
// help text in the form. The server's LunaXmlSerializer writes XML comments
// using the same attribute on the server side — the GUI does NOT need to
// reproduce that write behaviour (System.Xml.Serialization will round-trip
// the XML body without the comments, matching server load-then-save which
// regenerates comments on write).
[AttributeUsage(AttributeTargets.Property)]
public sealed class XmlCommentAttribute : Attribute
{
    public string Value { get; set; } = string.Empty;
}
