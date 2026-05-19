using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using LunaServerGui.SettingsCatalog;

namespace LunaServerGui.Services;

/// <summary>
/// XML reader + writer for one settings file. The serialization pipeline
/// is byte-equivalent to LmpCommon/Xml/LunaXmlSerializer.cs — same
/// XmlTextWriter formatting, same XmlComment-injection step, same re-format
/// pass — so a file written by the GUI is indistinguishable from one
/// written by the server. The matched-byte contract matters: the server's
/// SettingsBase.Load does a load-then-Save on every startup, and if the
/// GUI's write differs even in whitespace the server will rewrite the file
/// on next start, churning the operator's mtime and making backups noisy.
///
/// Atomic write: serialize to string, write to &lt;path&gt;.tmp, then
/// File.Move(.tmp, path, overwrite:true). On Windows + same volume this is
/// a metadata-only rename. Mirrors the FileHandler.WriteAtomic idiom used
/// in Stage 5.14c for AgencyState.
///
/// Backup: before writing, copy the existing file to
/// &lt;configDir&gt;/.backups/&lt;FileName&gt;.&lt;UTC-iso-timestamp&gt;.xml
/// so a botched edit can always be rolled back. Spec
/// §Validation-And-Safety-Rules: "automatically create a timestamped
/// backup before writing config files."
/// </summary>
public sealed class SettingsXmlService
{
    public const string BackupSubdirectory = ".backups";

    public object? Read(Type definitionType, string path)
    {
        ArgumentNullException.ThrowIfNull(definitionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path)) return null;

        using var reader = new StreamReader(path);
        var serializer = new XmlSerializer(definitionType);
        return serializer.Deserialize(reader);
    }

    /// <summary>
    /// Result of a <see cref="Write"/> call.
    /// </summary>
    /// <param name="BackupPath">Where the prior file was copied to; null if no
    /// pre-existing file was backed up (first save of a new file) OR if the
    /// write was skipped because the serialized content was byte-identical to
    /// the existing file.</param>
    /// <param name="Skipped">True when the serialized content matched the
    /// existing file byte-for-byte and the write was a no-op (no backup, no
    /// mtime touch). Mirrors the server's <c>ContentChecker</c> skip in
    /// LunaXmlSerializer.WriteToXmlFile.</param>
    public readonly record struct WriteResult(string? BackupPath, bool Skipped);

    /// <summary>
    /// Atomically write <paramref name="instance"/> to <paramref name="path"/>
    /// with a timestamped backup of the previous file (if any). Returns a
    /// <see cref="WriteResult"/> describing what actually happened. Callers
    /// surface the BackupPath to the operator and treat Skipped=true as a
    /// successful no-op.
    /// </summary>
    public WriteResult Write(object instance, string path)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var contents = SerializeToXml(instance);

        // Content-equality skip: mirrors LunaXmlSerializer.WriteToXmlFile's
        // ContentChecker.ContentsAreEqual gate. Without this, repeated Saves
        // with no actual diff still spam .backups/ with identical snapshots
        // and churn the operator's mtime. Read-only check up front so we
        // never even create the backup directory for a no-op.
        if (File.Exists(path))
        {
            try
            {
                var existing = File.ReadAllText(path);
                if (string.Equals(existing, contents, StringComparison.Ordinal))
                    return new WriteResult(BackupPath: null, Skipped: true);
            }
            catch (IOException)
            {
                // If we can't read it (locked by another process, etc.) fall
                // through and let the write attempt surface the real error.
            }
        }

        var backupPath = BackupIfExists(path);
        var tempPath = path + ".tmp";

        File.WriteAllText(tempPath, contents);
        try
        {
            // tempPath is always path + ".tmp" so they're on the same volume
            // by construction — File.Move is a metadata-only rename on NTFS.
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Best-effort: if Move failed the tmp file is dangling. Try to
            // remove it so the next Save attempt doesn't trip on it. Swallow
            // failure here because the original exception is what matters.
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
        return new WriteResult(backupPath, Skipped: false);
    }

    private static string? BackupIfExists(string path)
    {
        if (!File.Exists(path)) return null;

        var configDir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(configDir))
            throw new InvalidOperationException($"Cannot derive directory from path: {path}");

        var backupDir = Path.Combine(configDir, BackupSubdirectory);

        // Operator-accident guard: if .backups exists as a FILE (touched by
        // hand, broken antivirus, etc.) Directory.CreateDirectory throws an
        // IOException with a message that reads "save failed" to the
        // operator rather than "delete this stray file." Detect explicitly
        // so we can give an actionable error.
        if (File.Exists(backupDir))
            throw new InvalidOperationException(
                $"Backup target '{backupDir}' exists as a file, but the GUI needs it as a directory for save backups. " +
                $"Move or delete that file and try again.");

        Directory.CreateDirectory(backupDir);

        // UTC timestamp avoids cross-TZ ambiguity. Millisecond precision
        // (fff) prevents collision when the operator clicks Save twice in
        // the same second — without it the second backup throws because
        // File.Copy uses overwrite:false. ":" is illegal in Windows
        // filenames so the format uses "Z" as the timezone marker and no
        // separators between H/M/S/ms.
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
        var fileName = Path.GetFileName(path);
        var backupPath = Path.Combine(backupDir, $"{fileName}.{stamp}.bak.xml");
        File.Copy(path, backupPath, overwrite: false);
        return backupPath;
    }

    /// <summary>
    /// Ported from LmpCommon/Xml/LunaXmlSerializer.cs:82-155. Produces a
    /// formatted-indent XML document with XmlComment attributes injected
    /// before each property element via the WriteComments helper.
    /// </summary>
    public static string SerializeToXml(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        string returnString;
        using (var s = new Utf8StringWriter())
        using (var w = new XmlTextWriter(s))
        {
            w.Formatting = Formatting.Indented;
            var serializer = new XmlSerializer(instance.GetType());
            serializer.Serialize(w, instance);
            var tempString = WriteComments(instance, s.ToString());
            using var sw = new Utf8StringWriter();
            using var sr = new StringReader(tempString);
            using var xmlReader = new XmlTextReader(sr);
            var xmlWriter = XmlWriter.Create(sw, new XmlWriterSettings { Indent = true });
            while (xmlReader.Read())
            {
                switch (xmlReader.NodeType)
                {
                    case XmlNodeType.Element:
                        xmlWriter.WriteStartElement(xmlReader.Prefix, xmlReader.LocalName, xmlReader.NamespaceURI);
                        xmlWriter.WriteAttributes(xmlReader, true);
                        if (xmlReader.IsEmptyElement)
                            xmlWriter.WriteFullEndElement();
                        break;
                    case XmlNodeType.Text:
                        xmlWriter.WriteString(xmlReader.Value);
                        break;
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        xmlWriter.WriteWhitespace(xmlReader.Value);
                        break;
                    case XmlNodeType.CDATA:
                        xmlWriter.WriteCData(xmlReader.Value);
                        break;
                    case XmlNodeType.EntityReference:
                        xmlWriter.WriteEntityRef(xmlReader.Name);
                        break;
                    case XmlNodeType.XmlDeclaration:
                    case XmlNodeType.ProcessingInstruction:
                        xmlWriter.WriteProcessingInstruction(xmlReader.Name, xmlReader.Value);
                        break;
                    case XmlNodeType.DocumentType:
                        xmlWriter.WriteDocType(xmlReader.Name, xmlReader.GetAttribute("PUBLIC"), xmlReader.GetAttribute("SYSTEM"), xmlReader.Value);
                        break;
                    case XmlNodeType.Comment:
                        xmlWriter.WriteComment(xmlReader.Value);
                        break;
                    case XmlNodeType.EndElement:
                        xmlWriter.WriteFullEndElement();
                        break;
                }
            }
            xmlWriter.WriteEndDocument();
            xmlWriter.Flush();
            xmlWriter.Close();
            returnString = sw.ToString();
        }
        return returnString;
    }

    private static string WriteComments(object instance, string contents)
    {
        try
        {
            var propertyComments = GetPropertiesAndComments(instance);
            if (propertyComments.Count == 0) return contents;

            var doc = new XmlDocument();
            doc.LoadXml(contents);

            var parent = doc.SelectSingleNode(instance.GetType().Name);
            if (parent is null) return contents;

            var children = parent.ChildNodes.Cast<XmlNode>()
                .Where(n => propertyComments.ContainsKey(n.Name))
                .ToList();
            foreach (var child in children)
                parent.InsertBefore(doc.CreateComment(propertyComments[child.Name]), child);

            using var stringWriter = new Utf8StringWriter();
            doc.Save(stringWriter);
            return stringWriter.ToString();
        }
        catch
        {
            // Match server's "ignored" behaviour on comment-injection failure:
            // return the un-commented XML rather than fail the whole write.
            return contents;
        }
    }

    private static Dictionary<string, string> GetPropertiesAndComments(object instance)
    {
        return instance.GetType().GetProperties()
            .Where(p => p.GetCustomAttribute<XmlCommentAttribute>() is not null)
            .ToDictionary(
                p => p.Name,
                p => p.GetCustomAttribute<XmlCommentAttribute>()!.Value);
    }

    // [fix:BUG-038] Mirrors LmpCommon/Xml/LunaXmlSerializer.Utf8StringWriter so the
    // server↔GUI byte-equivalence contract documented in the class header survives the
    // prologue-encoding fix. StringWriter.Encoding is hard-coded to UTF-16, which
    // XmlSerializer / XmlDocument.Save inspect when writing the <?xml ?> declaration;
    // the disk write is UTF-8 no-BOM. Without this override, GUI-written files would
    // declare utf-16 while the server-written ones declare utf-8 → server's
    // load-then-Save re-rewrites the prologue on every restart, churning mtime + backups.
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
