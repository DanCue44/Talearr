using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using Chaptarr.Core.Test.Talearr.Recordings;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaCover;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Talearr.Metadata;

namespace Chaptarr.Core.Test.Talearr.Metadata
{
    // The writer and its MetadataService hook: what reaches the disk, and what is left alone.
    [TestFixture]
    public class TalearrSidecarCoverWriterFixture
    {
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "talearr-cover-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_folder, "A Book"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        private string CoverPath => Path.Combine(_folder, "A Book", "cover.jpg");

        private static IDiskProvider RealDisk()
        {
            var (disk, fake) = InterfaceFake<IDiskProvider>.Create();
            fake.On(nameof(IDiskProvider.FileExists), a => File.Exists((string)a[0]))
                .On(nameof(IDiskProvider.OpenReadStream), a => File.OpenRead((string)a[0]))
                .On(nameof(IDiskProvider.OpenWriteStream), a => File.Create((string)a[0]))
                .On(nameof(IDiskProvider.MoveFile), a =>
                {
                    File.Move((string)a[0], (string)a[1], (bool)a[2]);
                    return null;
                })
                .On(nameof(IDiskProvider.DeleteFile), a =>
                {
                    File.Delete((string)a[0]);
                    return null;
                });
            return disk;
        }

        private static IHttpClient Http(Dictionary<string, byte[]> images, List<string> requested)
        {
            var (client, fake) = InterfaceFake<IHttpClient>.Create();
            fake.On(nameof(IHttpClient.Get), a =>
            {
                var request = (HttpRequest)a[0];
                var url = request.Url.FullUri;
                requested.Add(url);
                if (!images.TryGetValue(url, out var data))
                {
                    throw new HttpException(new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), HttpStatusCode.NotFound));
                }

                return new HttpResponse(request, new HttpHeader(), data, HttpStatusCode.OK);
            });
            return client;
        }

        private static IMediaFileAttributeService NoPermissions()
        {
            var (service, fake) = InterfaceFake<IMediaFileAttributeService>.Create();
            fake.On(nameof(IMediaFileAttributeService.SetFilePermissions), _ => null);
            return service;
        }

        private static TalearrSidecarImage Sidecar(params string[] urls)
        {
            var candidates = urls.Select(u => new TalearrCoverCandidate(u, TalearrCoverSource.Edition, false, u)).ToList();
            return new TalearrSidecarImage("A Book/cover.jpg", candidates, "A Book");
        }

        private TalearrSidecarCoverWriter Writer(Dictionary<string, byte[]> images, List<string> requested)
        {
            return new TalearrSidecarCoverWriter(Http(images, requested), RealDisk(), NoPermissions(), LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void writes_the_chosen_image_and_leaves_no_temporary_file()
        {
            var requested = new List<string>();
            var writer = Writer(new Dictionary<string, byte[]> { ["https://covers.example.test/a.jpg"] = TalearrSidecarCoverFixture.Jpeg(400, 600) }, requested);

            var decision = writer.Decide(Sidecar("https://covers.example.test/a.jpg"), CoverPath);
            var written = writer.Save(decision, CoverPath);

            Assert.Multiple(() =>
            {
                Assert.That(written, Is.True);
                Assert.That(CoverImageFormats.Identify(CoverPath).Width, Is.EqualTo(400));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(CoverPath)), Is.EqualTo(new[] { CoverPath }));
            });
        }

        [Test]
        public void keeps_a_larger_cover_already_in_the_folder()
        {
            File.WriteAllBytes(CoverPath, TalearrSidecarCoverFixture.Jpeg(1000, 1000));
            var writer = Writer(new Dictionary<string, byte[]> { ["https://covers.example.test/a.jpg"] = TalearrSidecarCoverFixture.Jpeg(318, 424) }, new List<string>());

            var decision = writer.Decide(Sidecar("https://covers.example.test/a.jpg"), CoverPath);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Verdict, Is.EqualTo(TalearrCoverVerdict.KeepExisting));
                Assert.That(writer.Save(decision, CoverPath), Is.False);
                Assert.That(CoverImageFormats.Identify(CoverPath).Width, Is.EqualTo(1000));
            });
        }

        [Test]
        public void a_book_with_only_thumbnails_is_not_downloaded_again_on_the_next_scan()
        {
            var requested = new List<string>();
            var writer = Writer(new Dictionary<string, byte[]> { ["https://covers.example.test/thumb.jpg"] = TalearrSidecarCoverFixture.Jpeg(50, 67) }, requested);
            var sidecar = Sidecar("https://covers.example.test/thumb.jpg");

            var first = writer.Decide(sidecar, CoverPath);
            var second = writer.Decide(sidecar, CoverPath);

            Assert.Multiple(() =>
            {
                Assert.That(first.Verdict, Is.EqualTo(TalearrCoverVerdict.TooSmall));
                Assert.That(second.Verdict, Is.EqualTo(TalearrCoverVerdict.TooSmall));
                Assert.That(requested, Has.Count.EqualTo(1));
                Assert.That(File.Exists(CoverPath), Is.False);
            });
        }

        [Test]
        public void a_png_is_written_as_jpeg_into_cover_jpg()
        {
            var bytes = TalearrSidecarCoverWriter.MatchExtension(TalearrSidecarCoverFixture.Png(400, 600), CoverPath);

            Assert.That(CoverImageFormats.DetectFormat(bytes), Is.EqualTo(CoverImageFormats.Jpeg));
        }

        [Test]
        public void a_jpeg_is_written_unchanged()
        {
            var jpeg = TalearrSidecarCoverFixture.Jpeg(400, 600);

            Assert.That(TalearrSidecarCoverWriter.MatchExtension(jpeg, CoverPath), Is.SameAs(jpeg));
        }

        // MetadataService hook.
        private class FakeWriter : ITalearrSidecarCoverWriter
        {
            public TalearrCoverDecision Decision { get; set; }
            public List<string> Saved { get; } = new List<string>();

            public TalearrCoverDecision Decide(TalearrSidecarImage image, string fullPath) => Decision;

            public bool Save(TalearrCoverDecision decision, string fullPath)
            {
                Saved.Add(fullPath);
                File.WriteAllBytes(fullPath, decision.Image.Data);
                return true;
            }
        }

        private static MetadataService Service(FakeWriter writer, List<MetadataFile> upserted)
        {
            var (files, filesFake) = InterfaceFake<IMetadataFileService>.Create();
            filesFake.On(nameof(IMetadataFileService.Upsert), a =>
            {
                if (a[0] is MetadataFile file)
                {
                    upserted.Add(file);
                }

                return null;
            });

            var (renamer, renamerFake) = InterfaceFake<IOtherExtraFileRenamer>.Create();
            renamerFake.On(nameof(IOtherExtraFileRenamer.RenameOtherExtraFile), _ => null);

            var (http, _) = InterfaceFake<IHttpClient>.Create();

            return new MetadataService(null, RealDisk(), null, null, renamer, null, null, http, NoPermissions(), files, null,
                LogManager.GetCurrentClassLogger(), writer);
        }

        private class Consumer : IMetadata
        {
            public List<ImageFileResult> Images { get; set; } = new List<ImageFileResult>();
            public string Name => "Test";
            public Type ConfigContract => typeof(object);
            public NzbDrone.Core.ThingiProvider.ProviderMessage Message => null;
            public IEnumerable<NzbDrone.Core.ThingiProvider.ProviderDefinition> DefaultDefinitions => new List<NzbDrone.Core.ThingiProvider.ProviderDefinition>();
            public NzbDrone.Core.ThingiProvider.ProviderDefinition Definition { get; set; }
            public FluentValidation.Results.ValidationResult Test() => new FluentValidation.Results.ValidationResult();
            public object RequestAction(string stage, IDictionary<string, string> query) => null;
            public string GetFilenameAfterMove(Author author, BookFile bookFile, MetadataFile metadataFile) => throw new NotImplementedException();
            public string GetFilenameAfterMove(Author author, string bookPath, MetadataFile metadataFile) => throw new NotImplementedException();
            public MetadataFile FindMetadataFile(Author author, string path) => throw new NotImplementedException();
            public MetadataFileResult AuthorMetadata(Author author) => null;
            public MetadataFileResult BookMetadata(Author author, BookFile bookFile) => null;
            public List<ImageFileResult> AuthorImages(Author author) => new List<ImageFileResult>();
            public List<ImageFileResult> BookImages(Author author, BookFile bookFile) => Images;
        }

        private (Author Author, BookFile BookFile) Library()
        {
            var author = new Author { Id = 10, Name = "A Author", Path = _folder, AudiobookPath = _folder, EbookPath = _folder };
            var book = new Book { Id = 20, Title = "A Book" };
            var edition = new Edition { Id = 30, BookId = 20, Title = "A Book", Book = book };
            var bookFile = new BookFile { Id = 40, EditionId = 30, Edition = edition, MediaType = "audiobook", Path = Path.Combine(_folder, "A Book", "a.m4b"), Part = 1 };
            return (author, bookFile);
        }

        private static List<MetadataFile> ProcessBookImages(MetadataService service, IMetadata consumer, Author author, BookFile bookFile, List<MetadataFile> existing)
        {
            var method = typeof(MetadataService).GetMethod("ProcessBookImages", BindingFlags.Instance | BindingFlags.NonPublic);
            return (List<MetadataFile>)method.Invoke(service, new object[] { consumer, author, bookFile, existing });
        }

        private static string TrackedHash(string path, string source)
        {
            using (var stream = File.OpenRead(path))
            {
                return stream.SHA256Hash() + ":" + source.SHA256Hash();
            }
        }

        [Test]
        public void hook_writes_the_chosen_cover_and_tracks_it()
        {
            var (author, bookFile) = Library();
            var image = TalearrSidecarCoverFixture.Jpeg(500, 500);
            var sidecar = Sidecar("https://covers.example.test/a.jpg");
            var writer = new FakeWriter
            {
                Decision = new TalearrCoverDecision
                {
                    Verdict = TalearrCoverVerdict.Write,
                    Image = new TalearrCoverImage(sidecar.Candidates[0], image, 500, 500)
                }
            };

            var result = ProcessBookImages(Service(writer, new List<MetadataFile>()), new Consumer { Images = { sidecar } }, author, bookFile, new List<MetadataFile>());

            Assert.Multiple(() =>
            {
                Assert.That(writer.Saved, Is.EqualTo(new[] { CoverPath }));
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result[0].Hash, Is.EqualTo(TrackedHash(CoverPath, sidecar.SourceKey)));
            });
        }

        [Test]
        public void hook_writes_nothing_for_a_thumbnail()
        {
            var (author, bookFile) = Library();
            var writer = new FakeWriter { Decision = new TalearrCoverDecision { Verdict = TalearrCoverVerdict.TooSmall } };

            var result = ProcessBookImages(Service(writer, new List<MetadataFile>()), new Consumer { Images = { Sidecar("https://covers.example.test/thumb.jpg") } }, author, bookFile, new List<MetadataFile>());

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Empty);
                Assert.That(writer.Saved, Is.Empty);
                Assert.That(File.Exists(CoverPath), Is.False);
            });
        }

        [Test]
        public void hook_keeps_a_larger_tracked_cover_and_remembers_the_candidates()
        {
            var (author, bookFile) = Library();
            File.WriteAllBytes(CoverPath, TalearrSidecarCoverFixture.Jpeg(1000, 1000));
            var sidecar = Sidecar("https://covers.example.test/new.jpg");
            var tracked = new MetadataFile
            {
                Id = 60,
                AuthorId = 10,
                BookId = 20,
                Consumer = nameof(Consumer),
                Type = MetadataType.BookImage,
                RelativePath = "A Book/cover.jpg",
                Hash = TrackedHash(CoverPath, "https://covers.example.test/old.jpg")
            };
            var writer = new FakeWriter { Decision = new TalearrCoverDecision { Verdict = TalearrCoverVerdict.KeepExisting } };

            var result = ProcessBookImages(Service(writer, new List<MetadataFile>()), new Consumer { Images = { sidecar } }, author, bookFile, new List<MetadataFile> { tracked });

            Assert.Multiple(() =>
            {
                Assert.That(writer.Saved, Is.Empty);
                Assert.That(CoverImageFormats.Identify(CoverPath).Width, Is.EqualTo(1000));
                Assert.That(result, Is.EqualTo(new[] { tracked }));
                Assert.That(tracked.Hash, Is.EqualTo(TrackedHash(CoverPath, sidecar.SourceKey)));
            });
        }

        [Test]
        public void hook_never_touches_a_cover_it_did_not_write()
        {
            var (author, bookFile) = Library();
            var userCover = TalearrSidecarCoverFixture.Jpeg(60, 80);
            File.WriteAllBytes(CoverPath, userCover);
            var writer = new FakeWriter { Decision = new TalearrCoverDecision { Verdict = TalearrCoverVerdict.Write } };

            var result = ProcessBookImages(Service(writer, new List<MetadataFile>()), new Consumer { Images = { Sidecar("https://covers.example.test/a.jpg") } }, author, bookFile, new List<MetadataFile>());

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Empty);
                Assert.That(writer.Saved, Is.Empty);
                Assert.That(File.ReadAllBytes(CoverPath), Is.EqualTo(userCover));
            });
        }
    }
}
