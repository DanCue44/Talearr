using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Talearr.Audiobookshelf
{
    // TaleArr: the matcher's view of the library during one sync. Reads chaptarr.db through the
    // normal services only and memoizes per sync, so a large Audiobookshelf library costs one
    // query per root folder, one per author and one per identifier.
    public sealed class TalearrAbsBookLookup : ITalearrAbsBookLookup
    {
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IReadOnlyCollection<string> _rootFolderPaths;

        private readonly Dictionary<int, List<Book>> _booksByAuthor = new Dictionary<int, List<Book>>();
        private readonly Dictionary<int, Book> _booksById = new Dictionary<int, Book>();
        private Dictionary<string, List<Author>> _authorsByName;
        private List<(string Path, int BookId)> _files;

        public TalearrAbsBookLookup(IAuthorService authorService,
                                    IBookService bookService,
                                    IEditionService editionService,
                                    IMediaFileService mediaFileService,
                                    IReadOnlyCollection<string> rootFolderPaths)
        {
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _rootFolderPaths = rootFolderPaths ?? Array.Empty<string>();
        }

        public List<Book> GetBooksWithFilesUnder(string localPath)
        {
            if (localPath.IsNullOrWhiteSpace())
            {
                return new List<Book>();
            }

            LoadFiles();

            var bookIds = _files
                .Where(f => TalearrAbsItemReader.TryGetRelative(localPath, f.Path, out _))
                .Select(f => f.BookId)
                .Distinct()
                .ToList();

            return GetBooks(bookIds);
        }

        public List<Book> FindBooksByAsin(string asin)
        {
            return BooksForEditions(_editionService.GetEditionsByProviderAndId("az", asin));
        }

        public List<Book> FindBooksByIsbn(string isbn13)
        {
            var editions = new List<Edition>();
            foreach (var form in TalearrAbsItemReader.IsbnLookupForms(isbn13))
            {
                editions.AddRange(_editionService.GetEditionsByProviderAndId("isbn", form) ?? new List<Edition>());
            }

            return BooksForEditions(editions);
        }

        public List<Author> FindAuthors(string normalizedName)
        {
            if (normalizedName.IsNullOrWhiteSpace())
            {
                return new List<Author>();
            }

            if (_authorsByName == null)
            {
                _authorsByName = new Dictionary<string, List<Author>>(StringComparer.Ordinal);
                foreach (var author in _authorService.GetAllAuthors() ?? new List<Author>())
                {
                    var key = TalearrAbsMatcher.NormalizeName(author?.Name);
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    if (!_authorsByName.TryGetValue(key, out var list))
                    {
                        list = new List<Author>();
                        _authorsByName[key] = list;
                    }

                    list.Add(author);
                }
            }

            return _authorsByName.TryGetValue(normalizedName, out var found) ? found : new List<Author>();
        }

        public List<Book> GetBooksByAuthor(int authorId)
        {
            if (authorId <= 0)
            {
                return new List<Book>();
            }

            if (!_booksByAuthor.TryGetValue(authorId, out var books))
            {
                books = (_bookService.GetBooksByAuthor(authorId) ?? new List<Book>()).Where(b => b != null).ToList();
                _booksByAuthor[authorId] = books;
                foreach (var book in books)
                {
                    _booksById[book.Id] = book;
                }
            }

            return books;
        }

        private List<Book> BooksForEditions(IEnumerable<Edition> editions)
        {
            var bookIds = (editions ?? Enumerable.Empty<Edition>())
                .Where(e => e != null && e.BookId > 0)
                .Select(e => e.BookId)
                .Distinct()
                .ToList();

            return GetBooks(bookIds);
        }

        private List<Book> GetBooks(List<int> bookIds)
        {
            var missing = bookIds.Where(id => !_booksById.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                foreach (var book in _bookService.GetBooks(missing) ?? new List<Book>())
                {
                    if (book != null)
                    {
                        _booksById[book.Id] = book;
                    }
                }
            }

            return bookIds.Where(_booksById.ContainsKey).Select(id => _booksById[id]).ToList();
        }

        private void LoadFiles()
        {
            if (_files != null)
            {
                return;
            }

            var files = new List<BookFile>();
            foreach (var root in _rootFolderPaths.Where(r => r.IsNotNullOrWhiteSpace()).Distinct())
            {
                files.AddRange(_mediaFileService.GetFilesWithBasePath(root) ?? new List<BookFile>());
            }

            var editionIds = files.Where(f => f.EditionId > 0).Select(f => f.EditionId).Distinct().ToList();
            var bookByEdition = new Dictionary<int, int>();
            foreach (var chunk in editionIds.Chunk(500))
            {
                foreach (var edition in _editionService.GetEditions(chunk) ?? new List<Edition>())
                {
                    if (edition != null && edition.BookId > 0)
                    {
                        bookByEdition[edition.Id] = edition.BookId;
                    }
                }
            }

            _files = files
                .Where(f => f.Path.IsNotNullOrWhiteSpace() && bookByEdition.ContainsKey(f.EditionId))
                .Select(f => (f.Path, bookByEdition[f.EditionId]))
                .ToList();
        }
    }
}
