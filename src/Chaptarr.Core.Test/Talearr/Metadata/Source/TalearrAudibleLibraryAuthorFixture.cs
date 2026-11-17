using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    // Talearr #10: The Complete Thunder Series came from Audible with Dietmar Wehr's Audible author
    // id and the library author's Id, but no Hardcover author, so the add went to the old metadata
    // server and failed with "not found".
    [TestFixture]
    public class TalearrAudibleLibraryAuthorFixture
    {
        private const string Wehr = "hc:1071895";

        [Test]
        public void an_add_naming_a_library_author_uses_its_hardcover_id()
        {
            var book = Thunder(new Author { Id = 213, Name = "Dietmar Wehr", AudnexusAuthorId = "B00CM0V5H8" });

            AddBookService.FillLibraryAuthorProviderIds(book, Authors(Library()), LogManager.GetCurrentClassLogger());

            Assert.That(book.Author.HardcoverAuthorId, Is.EqualTo(Wehr));
        }

        [Test]
        public void an_add_with_a_hardcover_author_keeps_it()
        {
            var book = Thunder(new Author { Id = 213, Name = "Dietmar Wehr", HardcoverAuthorId = "hc:259462" });

            AddBookService.FillLibraryAuthorProviderIds(book, Authors(Library()), LogManager.GetCurrentClassLogger());

            Assert.That(book.Author.HardcoverAuthorId, Is.EqualTo("hc:259462"));
        }

        [Test]
        public void an_add_for_a_new_author_or_a_missing_one_is_left_alone()
        {
            var newAuthor = Thunder(new Author { Id = 0, Name = "Dietmar Wehr" });
            var missing = Thunder(new Author { Id = 999, Name = "Dietmar Wehr" });
            var authors = Authors(Library());

            AddBookService.FillLibraryAuthorProviderIds(newAuthor, authors, LogManager.GetCurrentClassLogger());
            AddBookService.FillLibraryAuthorProviderIds(missing, authors, LogManager.GetCurrentClassLogger());

            Assert.That(newAuthor.Author.HardcoverAuthorId, Is.Null);
            Assert.That(missing.Author.HardcoverAuthorId, Is.Null);
        }

        private static List<Author> Library()
        {
            return new List<Author> { new Author { Id = 213, Name = "Dietmar Wehr", HardcoverAuthorId = Wehr } };
        }

        private static Book Thunder(Author author)
        {
            return new Book { Title = "The Complete Thunder Series", Author = author };
        }

        private static IAuthorService Authors(List<Author> library)
        {
            var authors = DispatchProxy.Create<IAuthorService, AuthorByIdProxy>();
            ((AuthorByIdProxy)(object)authors).Authors = library;
            return authors;
        }

        public class AuthorByIdProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthor))
                {
                    var id = (int)args[0];
                    return Authors.FirstOrDefault(a => a.Id == id) ?? throw new ModelNotFoundException(typeof(Author), id);
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }
    }
}
