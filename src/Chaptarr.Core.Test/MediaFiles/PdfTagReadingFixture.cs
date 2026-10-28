using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Reflection;
using System.Text;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    // PDF document info (title, author, dates) read through PdfPig.
    [TestFixture]
    public class PdfTagReadingFixture
    {
        public class ThrowingProxy<T> : DispatchProxy
            where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new InvalidOperationException($"{typeof(T).Name}.{targetMethod?.Name} should not be called");
            }
        }

        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "talearr-pdf-tags-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_folder, recursive: true);
        }

        private static EBookTagService Subject()
        {
            return new EBookTagService(
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                DispatchProxy.Create<IFileMutationSafetyService, ThrowingProxy<IFileMutationSafetyService>>(),
                LogManager.GetLogger("test"));
        }

        // A one-page PDF with an /Info dictionary
        private static byte[] PdfWithInfo(string info)
        {
            var objects = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
                info
            };

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

            builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 4 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        private Dictionary<string, List<string>> Read(byte[] pdf)
        {
            var path = Path.Combine(_folder, "book.pdf");
            File.WriteAllBytes(path, pdf);
            return Subject().ReadAllTags(new FileSystem().FileInfo.FromFileName(path));
        }

        [Test]
        public void reads_the_document_info()
        {
            var tags = Read(PdfWithInfo("<< /Title (Dune) /Author (Frank Herbert) /Subject (Desert planet) /Keywords (sf, classic) " +
                                         "/Creator (Writer) /Producer (Printer) /CreationDate (D:19650801120000Z) /ModDate (D:20200102030405Z) >>"));

            Assert.That(tags["title"], Is.EqualTo(new[] { "Dune" }));
            Assert.That(tags["author"], Is.EqualTo(new[] { "Frank Herbert" }));
            Assert.That(tags["description"], Is.EqualTo(new[] { "Desert planet" }));
            Assert.That(tags["keywords"], Is.EqualTo(new[] { "sf, classic" }));
            Assert.That(tags["creator"], Is.EqualTo(new[] { "Writer" }));
            Assert.That(tags["producer"], Is.EqualTo(new[] { "Printer" }));
            Assert.That(tags["creation_date"], Is.EqualTo(new[] { "1965-08-01" }));
            Assert.That(tags["modification_date"], Is.EqualTo(new[] { "2020-01-02" }));
        }

        [Test]
        public void a_pdf_without_info_gives_no_tags()
        {
            var tags = Read(PdfWithInfo("<< >>"));

            Assert.That(tags.ContainsKey("title"), Is.False);
            Assert.That(tags.ContainsKey("creation_date"), Is.False);
        }
    }
}
