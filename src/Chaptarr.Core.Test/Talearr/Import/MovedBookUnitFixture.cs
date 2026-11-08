using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.RootFolders;
using Chaptarr.Core.Test.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Import
{
    // A book moved by hand to another folder is found at its new path while its old row is still
    // tracked. It is the same recording moved, not a second one.
    [TestFixture]
    public class MovedBookUnitFixture
    {
        private const string OldPath = "/audiobooks/Chaptarr/John Scalzi/Monsters of Ohio/Monsters of Ohio.m4b";
        private const string NewPath = "/audiobooks/audiobookshelf/John Scalzi/Monsters of Ohio/Monsters of Ohio.m4b";

        private Book _book;
        private Edition _edition;
        private List<BookFile> _files;
        private List<BookFile> _deleted;
        private HashSet<string> _onDisk;
        private HashSet<string> _visibleRoots;

        [SetUp]
        public void SetUp()
        {
            _book = new Book { Id = 14585, AuthorId = 9, Title = "Monsters of Ohio", TitleSlug = "monsters-of-ohio", BaseBookId = "hc:2509265", MediaType = BookMediaType.Audiobook };
            _edition = new Edition { Id = 50001, BookId = _book.Id, Title = "Monsters of Ohio", Monitored = true };
            _files = new List<BookFile> { new BookFile { Id = 1, EditionId = _edition.Id, Path = OldPath } };
            _deleted = new List<BookFile>();
            _onDisk = new HashSet<string> { NewPath };
            _visibleRoots = new HashSet<string> { "/audiobooks/Chaptarr", "/audiobooks/audiobookshelf" };
        }

        private BookUnitDestinationService Subject(bool withDisk = true)
        {
            var (mediaFiles, mediaFake) = InterfaceFake<IMediaFileService>.Create();
            mediaFake.On(nameof(IMediaFileService.GetFilesByBook), a => _files.Where(f => (int)a[0] == _book.Id).ToList())
                     .On(nameof(IMediaFileService.DeleteMany), a =>
                     {
                         var gone = (List<BookFile>)a[0];
                         _deleted.AddRange(gone);
                         _files.RemoveAll(gone.Contains);
                         return null;
                     });

            var (books, booksFake) = InterfaceFake<IBookService>.Create();
            booksFake.On(nameof(IBookService.GetBook), a => _book)
                     .On(nameof(IBookService.GetBooksByAuthor), a => new List<Book> { _book });

            var (editions, editionsFake) = InterfaceFake<IEditionService>.Create();
            editionsFake.On(nameof(IEditionService.GetEditionsByBook), a => new List<Edition> { _edition })
                        .On(nameof(IEditionService.GetEdition), a => _edition);

            // Cloning a second recording writes to the database; reaching it is what these tests look for.
            var (database, databaseFake) = InterfaceFake<IMainDatabase>.Create();
            databaseFake.On(nameof(IMainDatabase.OpenConnection), a => throw new CloneAttempted());

            var (disk, diskFake) = InterfaceFake<IDiskProvider>.Create();
            diskFake.On(nameof(IDiskProvider.FolderExists), a => _visibleRoots.Contains((string)a[0]))
                    .On(nameof(IDiskProvider.FileExistsCanonical), a => _onDisk.Contains((string)a[0]));

            var (roots, rootsFake) = InterfaceFake<IRootFolderService>.Create();
            rootsFake.On(nameof(IRootFolderService.GetBestRootFolder), a =>
                new[] { "/audiobooks/Chaptarr", "/audiobooks/audiobookshelf" }
                    .Where(r => ((string)a[0]).StartsWith(r + "/"))
                    .Select(r => new RootFolder { Path = r })
                    .FirstOrDefault());

            return new BookUnitDestinationService(mediaFiles, books, editions, database,
                LogManager.GetCurrentClassLogger(), withDisk ? disk : null, withDisk ? roots : null);
        }

        private sealed class CloneAttempted : System.Exception
        {
        }

        private (int BookId, int EditionId) Resolve(BookUnitDestinationService subject, string path)
        {
            return subject.ResolveDestinationForUnit(_book, _edition, subject.BuildRootUnitKeyWithExtension(path, _edition.Title, _book.MediaType));
        }

        [Test]
        public void a_moved_book_keeps_its_recording_and_drops_the_old_row()
        {
            var dest = Resolve(Subject(), NewPath);

            Assert.That(dest, Is.EqualTo((_book.Id, _edition.Id)));
            Assert.That(_deleted.Select(f => f.Path), Is.EqualTo(new[] { OldPath }));
        }

        [Test]
        public void a_second_copy_while_the_first_is_still_there_is_another_recording()
        {
            _onDisk.Add(OldPath);

            Assert.Throws<CloneAttempted>(() => Resolve(Subject(), NewPath), "another recording is cloned");
            Assert.That(_deleted, Is.Empty);
        }

        [Test]
        public void a_root_folder_that_is_not_visible_is_not_read_as_a_move()
        {
            _visibleRoots.Remove("/audiobooks/Chaptarr");

            Assert.Throws<CloneAttempted>(() => Resolve(Subject(), NewPath), "a missing mount must not drop the old row");
            Assert.That(_deleted, Is.Empty);
        }
    }
}
