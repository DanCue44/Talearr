using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Talearr.Audiobook.M4b;
using NzbDrone.Core.Talearr.ReadOnlyLibrary;

namespace Chaptarr.Core.Test.Talearr.ReadOnlyLibrary
{
    // The read-only guard is process-wide, so these tests never run alongside others.
    [TestFixture]
    [NonParallelizable]
    public class TalearrReadOnlyLibraryFixture
    {
        private string _tempRoot;
        private string _readOnlyRoot;
        private string _writableRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"talearr-readonly-{Guid.NewGuid():N}");
            _readOnlyRoot = Path.Combine(_tempRoot, "Libation");
            _writableRoot = Path.Combine(_tempRoot, "Audiobooks");
            Directory.CreateDirectory(_readOnlyRoot);
            Directory.CreateDirectory(_writableRoot);
            ReadOnlyPathGuard.SetRoots(new[] { _readOnlyRoot });
        }

        [TearDown]
        public void TearDown()
        {
            ReadOnlyPathGuard.SetRoots(Array.Empty<string>());

            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }

        [Test]
        public void should_treat_the_root_and_everything_inside_as_read_only()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ReadOnlyPathGuard.IsReadOnly(_readOnlyRoot), Is.True);
                Assert.That(ReadOnlyPathGuard.IsReadOnly(Path.Combine(_readOnlyRoot, "Author", "Book.m4b")), Is.True);
                Assert.That(ReadOnlyPathGuard.IsReadOnly(_readOnlyRoot + "2"), Is.False);
                Assert.That(ReadOnlyPathGuard.IsReadOnly(Path.Combine(_writableRoot, "Book.m4b")), Is.False);
                Assert.That(ReadOnlyPathGuard.IsReadOnly(null), Is.False);
            });
        }

        [Test]
        public void disk_provider_should_refuse_to_change_a_read_only_folder()
        {
            var book = Path.Combine(_readOnlyRoot, "Book.m4b");
            File.WriteAllText(book, "audio");
            File.WriteAllText(Path.Combine(_writableRoot, "x"), "audio");
            var disk = new NzbDrone.Windows.Disk.DiskProvider();

            Assert.Throws<ReadOnlyPathException>(() => disk.DeleteFile(book));
            Assert.Throws<ReadOnlyPathException>(() => disk.WriteAllText(Path.Combine(_readOnlyRoot, "metadata.json"), "{}"));
            Assert.Throws<ReadOnlyPathException>(() => disk.MoveFile(book, Path.Combine(_writableRoot, "Book.m4b")));
            Assert.Throws<ReadOnlyPathException>(() => disk.CopyFile(Path.Combine(_writableRoot, "x"), Path.Combine(_readOnlyRoot, "x")));
            Assert.Throws<ReadOnlyPathException>(() => disk.CreateFolder(Path.Combine(_readOnlyRoot, "New Author")));
            Assert.Throws<ReadOnlyPathException>(() => disk.DeleteFolder(_readOnlyRoot, true));

            Assert.That(File.Exists(book), Is.True);

            // Reading, and creating a folder that already exists, still work.
            Assert.That(disk.ReadAllText(book), Is.EqualTo("audio"));
            Assert.DoesNotThrow(() => disk.CreateFolder(_readOnlyRoot));

            // A writable folder is unaffected.
            var other = Path.Combine(_writableRoot, "Other.m4b");
            disk.WriteAllText(other, "audio");
            disk.DeleteFile(other);
            Assert.That(File.Exists(other), Is.False);
        }

        [Test]
        public void should_not_offer_conversion_in_a_read_only_folder()
        {
            var files = new List<BookFile>
            {
                new BookFile { Id = 1, EditionId = 1, Path = Path.Combine(_readOnlyRoot, "Author", "Book", "01.mp3"), Part = 1, PartCount = 2 },
                new BookFile { Id = 2, EditionId = 1, Path = Path.Combine(_readOnlyRoot, "Author", "Book", "02.mp3"), Part = 2, PartCount = 2 }
            };

            var plan = M4bLibraryPlanner.Plan(files).Single();

            Assert.That(plan.CanConvert, Is.False);
            Assert.That(plan.SkipReason, Does.Contain("read-only"));
        }

        [Test]
        public void should_not_grab_a_replacement_for_a_copy_in_a_read_only_folder()
        {
            var subject = new TalearrReadOnlyLibrarySpecification(LogManager.GetCurrentClassLogger());

            var readOnlyBook = new Book
            {
                Title = "Bad Blood",
                LazyBookFiles = new LazyLoaded<List<BookFile>>(new List<BookFile> { new BookFile { Path = Path.Combine(_readOnlyRoot, "Bad Blood.m4b") } })
            };
            var writableBook = new Book
            {
                Title = "Killer Instinct",
                LazyBookFiles = new LazyLoaded<List<BookFile>>(new List<BookFile> { new BookFile { Path = Path.Combine(_writableRoot, "Killer Instinct.m4b") } })
            };
            var missingBook = new Book { Title = "All In", LazyBookFiles = new LazyLoaded<List<BookFile>>(new List<BookFile>()) };

            Assert.That(subject.IsSatisfiedBy(new RemoteBook { Books = new List<Book> { readOnlyBook } }, null).Accepted, Is.False);
            Assert.That(subject.IsSatisfiedBy(new RemoteBook { Books = new List<Book> { writableBook } }, null).Accepted, Is.True);
            Assert.That(subject.IsSatisfiedBy(new RemoteBook { Books = new List<Book> { missingBook } }, null).Accepted, Is.True);
        }

        [Test]
        public void service_should_store_the_flag_and_redirect_new_files_to_a_writable_root()
        {
            ReadOnlyPathGuard.SetRoots(Array.Empty<string>());

            var config = new InMemoryConfigRepository();
            var rootFolders = new[]
            {
                new RootFolder { Id = 1, Path = _readOnlyRoot, FolderType = FolderType.Audiobook },
                new RootFolder { Id = 2, Path = Path.Combine(_tempRoot, "Ebooks"), FolderType = FolderType.Ebook },
                new RootFolder { Id = 3, Path = _writableRoot, FolderType = FolderType.Audiobook }
            };
            var subject = new TalearrReadOnlyLibraryService(config.Proxy, RootFolders(rootFolders), Config(), LogManager.GetCurrentClassLogger());

            subject.SetReadOnly(_readOnlyRoot, true);

            Assert.That(subject.IsReadOnly(Path.Combine(_readOnlyRoot, "Author")), Is.True);
            Assert.That(TalearrReadOnlyLibraryService.IsStoredReadOnly(config.Proxy, _readOnlyRoot), Is.True);

            var redirected = subject.RedirectForWrite(Path.Combine(_readOnlyRoot, "Jennifer Lynn Barnes"), BookMediaType.Audiobook);
            Assert.That(redirected, Is.EqualTo(Path.Combine(_writableRoot, "Jennifer Lynn Barnes")));

            subject.SetReadOnly(_readOnlyRoot, false);
            Assert.That(subject.IsReadOnly(Path.Combine(_readOnlyRoot, "Author")), Is.False);
        }

        [Test]
        public void redirect_should_explain_when_no_writable_root_exists()
        {
            ReadOnlyPathGuard.SetRoots(Array.Empty<string>());

            var config = new InMemoryConfigRepository();
            var rootFolders = new[] { new RootFolder { Id = 1, Path = _readOnlyRoot, FolderType = FolderType.Audiobook } };
            var subject = new TalearrReadOnlyLibraryService(config.Proxy, RootFolders(rootFolders), Config(), LogManager.GetCurrentClassLogger());
            subject.SetReadOnly(_readOnlyRoot, true);

            var ex = Assert.Throws<ReadOnlyPathException>(() => subject.RedirectForWrite(Path.Combine(_readOnlyRoot, "Author"), BookMediaType.Audiobook));
            Assert.That(ex.Message, Does.Contain("no writable audiobook root folder"));
        }

        [Test]
        public void a_removed_root_folder_is_no_longer_protected()
        {
            ReadOnlyPathGuard.SetRoots(Array.Empty<string>());

            var config = new InMemoryConfigRepository();
            config.Upsert(TalearrReadOnlyLibraryService.ConfigKey, $"[\"{_readOnlyRoot.Replace("\\", "\\\\")}\"]");
            var subject = new TalearrReadOnlyLibraryService(config.Proxy, RootFolders(), Config(), LogManager.GetCurrentClassLogger());

            Assert.That(subject.IsReadOnly(_readOnlyRoot), Is.False);
        }

        private static IRootFolderService RootFolders(params RootFolder[] rootFolders)
        {
            var proxy = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)proxy).Folders = rootFolders.ToList();
            return proxy;
        }

        private static IConfigService Config()
        {
            return DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
        }

        public class RootFolderServiceProxy : DispatchProxy
        {
            public List<RootFolder> Folders { get; set; } = new List<RootFolder>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.Name == nameof(IRootFolderService.All) ? Folders : throw new NotSupportedException(targetMethod.Name);
            }
        }

        public class ConfigServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.ReturnType == typeof(string) ? null : throw new NotSupportedException(targetMethod.Name);
            }
        }

        private sealed class InMemoryConfigRepository
        {
            public InMemoryConfigRepository()
            {
                Proxy = DispatchProxy.Create<IConfigRepository, ConfigRepositoryProxy>();
                ((ConfigRepositoryProxy)(object)Proxy).Store = Values;
            }

            public Dictionary<string, string> Values { get; } = new Dictionary<string, string>();

            public IConfigRepository Proxy { get; }

            public void Upsert(string key, string value) => Values[key] = value;
        }

        public class ConfigRepositoryProxy : DispatchProxy
        {
            public Dictionary<string, string> Store { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case nameof(IConfigRepository.Get):
                        var key = (string)args[0];
                        return Store.TryGetValue(key, out var value) ? new Config { Key = key, Value = value } : null;
                    case nameof(IConfigRepository.Upsert):
                        Store[(string)args[0]] = (string)args[1];
                        return new Config { Key = (string)args[0], Value = (string)args[1] };
                    default:
                        throw new NotSupportedException(targetMethod.Name);
                }
            }
        }
    }
}
