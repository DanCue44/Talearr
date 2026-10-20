using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;

namespace Chaptarr.Core.Test.MediaFiles.TalearrImport
{
    /// <summary>
    /// Builds small synthetic archives for the Talearr import tests. Nothing here comes from a
    /// real release: the structure (a zip holding a rar set plus .nfo/.diz, multi-volume rar,
    /// 7z) mirrors what scene ebook releases look like, the contents are made up.
    /// </summary>
    public static class TestArchives
    {
        public static byte[] Epub(string language, string title = "Synthetic Test Book")
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var mimetype = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using (var writer = new StreamWriter(mimetype.Open(), new UTF8Encoding(false)))
                {
                    writer.Write("application/epub+zip");
                }

                AddText(zip, "META-INF/container.xml",
                    "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
                    "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");

                var languageElement = language == null ? string.Empty : $"<dc:language>{language}</dc:language>";
                AddText(zip, "OEBPS/content.opf",
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                    "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\" unique-identifier=\"id\">" +
                    "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">" +
                    $"{languageElement}<dc:title>{title}</dc:title><dc:creator opf:role=\"aut\">Test Author</dc:creator>" +
                    "<dc:identifier id=\"id\">urn:uuid:00000000-0000-0000-0000-000000000000</dc:identifier>" +
                    "</metadata><manifest><item id=\"c1\" href=\"chapter-1.xhtml\" media-type=\"application/xhtml+xml\"/></manifest>" +
                    "<spine><itemref idref=\"c1\"/></spine></package>");

                AddText(zip, "OEBPS/chapter-1.xhtml",
                    "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Synthetic test content.</p></body></html>");
            }

            return buffer.ToArray();
        }

        public static byte[] Zip(params (string Name, byte[] Data)[] entries)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, data) in entries)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(data, 0, data.Length);
                }
            }

            return buffer.ToArray();
        }

        public static byte[] SevenZip(params (string Name, byte[] Data)[] entries)
        {
            using var buffer = new MemoryStream();
            using (var writer = WriterFactory.OpenWriter(buffer, ArchiveType.SevenZip, new SevenZipWriterOptions { LeaveStreamOpen = true }))
            {
                foreach (var (name, data) in entries)
                {
                    using var source = new MemoryStream(data);
                    writer.Write(name, source, DateTime.UtcNow);
                }
            }

            return buffer.ToArray();
        }

        public static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

        /// <summary>
        /// A minimal BOOKMOBI file: PalmDB header, record 0 with a PalmDOC + MOBI header and an
        /// EXTH block that carries the title, author and (optionally) EXTH 524 language.
        /// </summary>
        public static byte[] Mobi(string language, string title = "Synthetic Test Book")
        {
            var exthRecords = new List<(uint Type, byte[] Data)>
            {
                (100, Encoding.UTF8.GetBytes("Test Author"))
            };

            if (language != null)
            {
                exthRecords.Add((524, Encoding.UTF8.GetBytes(language)));
            }

            var exth = new List<byte>();
            var exthBodyLength = 12 + exthRecords.Sum(r => 8 + r.Data.Length);
            exth.AddRange(Encoding.ASCII.GetBytes("EXTH"));
            exth.AddRange(BigEndian((uint)exthBodyLength));
            exth.AddRange(BigEndian((uint)exthRecords.Count));
            foreach (var (type, data) in exthRecords)
            {
                exth.AddRange(BigEndian(type));
                exth.AddRange(BigEndian((uint)(8 + data.Length)));
                exth.AddRange(data);
            }

            const int mobiHeaderLength = 232;
            var titleBytes = Encoding.UTF8.GetBytes(title);
            var record0 = new byte[16 + mobiHeaderLength + exth.Count + titleBytes.Length];

            // PalmDOC header: no compression, no encryption.
            Array.Copy(BigEndian16(1), 0, record0, 0, 2);

            Encoding.ASCII.GetBytes("MOBI").CopyTo(record0, 16);
            Array.Copy(BigEndian((uint)mobiHeaderLength), 0, record0, 20, 4);
            Array.Copy(BigEndian(2u), 0, record0, 24, 4);
            Array.Copy(BigEndian(65001u), 0, record0, 28, 4);
            Array.Copy(BigEndian(6u), 0, record0, 36, 4);
            Array.Copy(BigEndian((uint)(16 + mobiHeaderLength + exth.Count)), 0, record0, 0x54, 4);
            Array.Copy(BigEndian((uint)titleBytes.Length), 0, record0, 0x58, 4);
            Array.Copy(BigEndian(0x40u), 0, record0, 0x80, 4);
            exth.ToArray().CopyTo(record0, 16 + mobiHeaderLength);
            titleBytes.CopyTo(record0, 16 + mobiHeaderLength + exth.Count);

            var record1 = new byte[] { 0xE9, 0x8E, 0x0D, 0x0A };

            const int recordListStart = 78;
            const int recordCount = 2;
            var record0Offset = recordListStart + (recordCount * 8) + 2;
            var record1Offset = record0Offset + record0.Length;

            var file = new byte[record1Offset + record1.Length];
            Encoding.ASCII.GetBytes("Synthetic_Test_Book").CopyTo(file, 0);
            Encoding.ASCII.GetBytes("BOOKMOBI").CopyTo(file, 0x3C);
            Array.Copy(BigEndian16(recordCount), 0, file, 76, 2);
            Array.Copy(BigEndian((uint)record0Offset), 0, file, recordListStart, 4);
            Array.Copy(BigEndian((uint)record1Offset), 0, file, recordListStart + 8, 4);
            record0.CopyTo(file, record0Offset);
            record1.CopyTo(file, record1Offset);

            return file;
        }

        private static byte[] BigEndian(uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }

        private static byte[] BigEndian16(ushort value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }

        /// <summary>
        /// A one-page PDF. <paramref name="catalogLanguage"/> goes into the catalog /Lang entry,
        /// <paramref name="xmpLanguage"/> into an uncompressed XMP packet.
        /// </summary>
        public static byte[] Pdf(string catalogLanguage, string xmpLanguage = null)
        {
            var objects = new List<string>();
            var lang = catalogLanguage == null ? string.Empty : $" /Lang ({catalogLanguage})";
            var metadataRef = xmpLanguage == null ? string.Empty : " /Metadata 4 0 R";
            objects.Add($"<< /Type /Catalog /Pages 2 0 R{lang}{metadataRef} >>");
            objects.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");

            if (xmpLanguage != null)
            {
                var xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
                          "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                          "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
                          $"<dc:language><rdf:Bag><rdf:li>{xmpLanguage}</rdf:li></rdf:Bag></dc:language>" +
                          "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
                objects.Add($"<< /Type /Metadata /Subtype /XML /Length {Encoding.ASCII.GetByteCount(xmp)} >>\nstream\n{xmp}\nendstream");
            }

            var builder = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (var i = 0; i < objects.Count; i++)
            {
                offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
                builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }

            var xref = Encoding.ASCII.GetByteCount(builder.ToString());
            builder.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                builder.Append($"{offset:D10} 00000 n \n");
            }

            builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        /// <summary>
        /// A single-volume RAR 4 archive with stored (uncompressed) entries.
        /// </summary>
        public static byte[] Rar(params (string Name, byte[] Data)[] entries)
        {
            return RarVolumes(entries, 1).Single();
        }

        /// <summary>
        /// A RAR 4 set with stored entries, each entry split evenly across
        /// <paramref name="volumeCount"/> volumes (new-style .partN.rar numbering).
        /// </summary>
        public static List<byte[]> RarVolumes((string Name, byte[] Data)[] entries, int volumeCount)
        {
            var volumes = new List<byte[]>();

            for (var volume = 0; volume < volumeCount; volume++)
            {
                using var buffer = new MemoryStream();
                buffer.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });

                ushort mainFlags = 0;
                if (volumeCount > 1)
                {
                    mainFlags |= 0x0001 | 0x0010;
                    if (volume == 0)
                    {
                        mainFlags |= 0x0100;
                    }
                }

                WriteBlock(buffer, 0x73, mainFlags, new byte[6]);

                foreach (var (name, data) in entries)
                {
                    var chunk = Split(data, volumeCount, volume);
                    ushort fileFlags = 0x8000;
                    if (volumeCount > 1 && volume > 0)
                    {
                        fileFlags |= 0x0001;
                    }

                    if (volumeCount > 1 && volume < volumeCount - 1)
                    {
                        fileFlags |= 0x0002;
                    }

                    // The last part carries the CRC of the whole file, earlier parts the CRC of
                    // their own slice, as rar itself writes them.
                    var crc = volume == volumeCount - 1 ? Crc32(data) : Crc32(chunk);
                    var nameBytes = Encoding.UTF8.GetBytes(name.Replace('\\', '/'));

                    using var header = new MemoryStream();
                    using (var writer = new BinaryWriter(header, Encoding.UTF8, leaveOpen: true))
                    {
                        writer.Write((uint)chunk.Length);
                        writer.Write((uint)data.Length);
                        writer.Write((byte)3);
                        writer.Write(crc);
                        writer.Write(0x5310070Du);
                        writer.Write((byte)20);
                        writer.Write((byte)0x30);
                        writer.Write((ushort)nameBytes.Length);
                        writer.Write(0x000081A4u);
                        writer.Write(nameBytes);
                    }

                    WriteBlock(buffer, 0x74, fileFlags, header.ToArray());
                    buffer.Write(chunk);
                }

                ushort endFlags = 0x4000;
                if (volume < volumeCount - 1)
                {
                    endFlags |= 0x0001;
                }

                WriteBlock(buffer, 0x7B, endFlags, Array.Empty<byte>());
                volumes.Add(buffer.ToArray());
            }

            return volumes;
        }

        private static byte[] Split(byte[] data, int count, int index)
        {
            var size = (data.Length + count - 1) / count;
            var start = Math.Min(data.Length, size * index);
            var length = Math.Min(size, data.Length - start);
            return data.Skip(start).Take(length).ToArray();
        }

        private static void WriteBlock(Stream output, byte type, ushort flags, byte[] body)
        {
            var size = (ushort)(7 + body.Length);
            using var headerBytes = new MemoryStream();
            headerBytes.WriteByte(type);
            headerBytes.Write(BitConverter.GetBytes(flags));
            headerBytes.Write(BitConverter.GetBytes(size));
            headerBytes.Write(body);

            var crcInput = headerBytes.ToArray();
            var crc = (ushort)(Crc32(crcInput) & 0xFFFF);

            output.Write(BitConverter.GetBytes(crc));
            output.Write(crcInput);
        }

        public static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var i = 0; i < 8; i++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }

            return ~crc;
        }

        private static void AddText(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}
