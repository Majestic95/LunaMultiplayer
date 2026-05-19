using LmpCommon.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace LmpCommonTest
{
    /// <summary>
    /// Pins the XML-prologue-encoding contract for <see cref="LunaXmlSerializer"/>
    /// after the BUG-038 fix. The disk byte encoding is UTF-8 no-BOM (via
    /// <c>File.WriteAllText</c>'s default); the declared prologue encoding must
    /// agree with that. Prior to the fix the prologue claimed utf-16 because
    /// <see cref="StringWriter"/>'s hard-coded <see cref="StringWriter.Encoding"/>
    /// returned UTF-16, which both <see cref="XmlSerializer"/> and
    /// <see cref="System.Xml.XmlDocument"/> inspect when writing the declaration.
    /// </summary>
    [TestClass]
    public class LunaXmlSerializerTest
    {
        public sealed class SampleSettings
        {
            public string ServerName { get; set; } = "Test Server";
            public int Port { get; set; } = 8800;
            public bool AllowCheats { get; set; } = false;
        }

        public sealed class SettingsWithComment
        {
            [XmlComment(Value = "Human-friendly server name")]
            public string ServerName { get; set; } = "Test";

            [XmlComment(Value = "Listen port")]
            public int Port { get; set; } = 8800;
        }

        [TestMethod]
        public void SerializeToXml_PrologueClaimsUtf8()
        {
            var content = LunaXmlSerializer.SerializeToXml(new SampleSettings());

            // Tolerate either single-or-double-quoted attributes; emitted prologue uses
            // double quotes today but we don't want the test pinned to that incidental detail.
            Assert.IsTrue(
                content.Contains("encoding=\"utf-8\"") || content.Contains("encoding='utf-8'"),
                $"Prologue should declare utf-8 encoding to match the on-disk bytes. Got: {content.Substring(0, Math.Min(120, content.Length))}");

            Assert.IsFalse(
                content.Contains("encoding=\"utf-16\"") || content.Contains("encoding='utf-16'"),
                "Prologue should not declare utf-16 — the disk write is UTF-8 no-BOM.");
        }

        [TestMethod]
        public void SerializeToXml_WithXmlComments_PrologueClaimsUtf8()
        {
            // The comment-injection path (WriteComments) round-trips through a separate
            // StringWriter via XmlDocument.Save, which independently inspects writer
            // encoding. Both code paths must agree.
            var content = LunaXmlSerializer.SerializeToXml(new SettingsWithComment());

            Assert.IsTrue(
                content.Contains("encoding=\"utf-8\"") || content.Contains("encoding='utf-8'"),
                $"Prologue from the WriteComments path should declare utf-8. Got: {content.Substring(0, Math.Min(120, content.Length))}");

            // Sanity: the comment we asked for should still appear in the output.
            Assert.IsTrue(content.Contains("Human-friendly server name"),
                "WriteComments path should still inject the [XmlComment] strings.");
        }

        [TestMethod]
        public void WriteToXmlFile_DiskBytesAreUtf8_AndMatchPrologue()
        {
            var path = Path.Combine(Path.GetTempPath(), $"luna_xml_test_{Guid.NewGuid():N}.xml");
            try
            {
                LunaXmlSerializer.WriteToXmlFile(new SampleSettings { ServerName = "Probe" }, path);

                var bytes = File.ReadAllBytes(path);
                Assert.IsTrue(bytes.Length > 0, "File should not be empty.");

                // Disk is UTF-8 no-BOM (File.WriteAllText default). For ASCII content the
                // distinguishing feature is "no UTF-16 BOM (FF FE / FE FF) and no double
                // null bytes between ASCII chars."
                Assert.IsFalse(bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE,
                    "UTF-16 LE BOM should not be present.");
                Assert.IsFalse(bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF,
                    "UTF-16 BE BOM should not be present.");

                // Heuristic: in UTF-16, ASCII letters are followed by a 0x00 byte. In our
                // ASCII-only payload that should never appear in UTF-8.
                var sawNullBetweenAscii = false;
                for (var i = 0; i + 1 < Math.Min(bytes.Length, 128); i++)
                {
                    if (bytes[i] >= 0x20 && bytes[i] < 0x7F && bytes[i + 1] == 0x00)
                    {
                        sawNullBetweenAscii = true;
                        break;
                    }
                }
                Assert.IsFalse(sawNullBetweenAscii, "Disk bytes look UTF-16 encoded.");

                // Decoded as UTF-8, the file should be readable XML with a utf-8 prologue.
                var text = Encoding.UTF8.GetString(bytes);
                Assert.IsTrue(
                    text.Contains("encoding=\"utf-8\"") || text.Contains("encoding='utf-8'"),
                    $"On-disk prologue should declare utf-8. First 200 chars: {text.Substring(0, Math.Min(200, text.Length))}");
                Assert.IsTrue(text.Contains("<ServerName>Probe</ServerName>"),
                    "Round-trip should preserve the field value.");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        public void ReadXmlFromPath_LoadsUtf16PrologueFile_BackwardCompat()
        {
            // Pre-fix files exist on operators' disks today with a utf-16 prologue but
            // UTF-8 bytes. The fix must not break loading those files — that's the
            // upgrade-lens path. .NET XmlSerializer through StreamReader tolerates the
            // mismatch by auto-detecting from BOM/content; pin that here so any future
            // serializer swap can't regress the read side silently.
            var path = Path.Combine(Path.GetTempPath(), $"luna_xml_legacy_{Guid.NewGuid():N}.xml");
            try
            {
                var legacy =
                    "<?xml version=\"1.0\" encoding=\"utf-16\"?>\n" +
                    "<SampleSettings>\n" +
                    "  <ServerName>LegacyName</ServerName>\n" +
                    "  <Port>8801</Port>\n" +
                    "  <AllowCheats>true</AllowCheats>\n" +
                    "</SampleSettings>\n";
                // Write as UTF-8 no-BOM to mimic the pre-fix on-disk state.
                File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(legacy));

                var loaded = LunaXmlSerializer.ReadXmlFromPath<SampleSettings>(path);

                Assert.IsNotNull(loaded, "Pre-fix legacy file should still deserialize after the fix.");
                Assert.AreEqual("LegacyName", loaded.ServerName);
                Assert.AreEqual(8801, loaded.Port);
                Assert.IsTrue(loaded.AllowCheats);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        public void WriteThenRead_RoundTripsValues()
        {
            var path = Path.Combine(Path.GetTempPath(), $"luna_xml_rt_{Guid.NewGuid():N}.xml");
            try
            {
                var original = new SampleSettings { ServerName = "Round", Port = 9999, AllowCheats = true };
                LunaXmlSerializer.WriteToXmlFile(original, path);

                var loaded = LunaXmlSerializer.ReadXmlFromPath<SampleSettings>(path);

                Assert.IsNotNull(loaded);
                Assert.AreEqual(original.ServerName, loaded.ServerName);
                Assert.AreEqual(original.Port, loaded.Port);
                Assert.AreEqual(original.AllowCheats, loaded.AllowCheats);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
