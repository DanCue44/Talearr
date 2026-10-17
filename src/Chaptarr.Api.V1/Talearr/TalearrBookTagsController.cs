using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Tags;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrBookTagsResource
    {
        public int BookId { get; set; }
        public List<int> Tags { get; set; } = new List<int>();
    }

    public class TalearrBookTagsBody
    {
        public List<int> Tags { get; set; }
    }

    public class TalearrBookTagsEditorBody
    {
        public List<int> BookIds { get; set; }
        public List<int> Tags { get; set; }
        public ApplyTags ApplyTags { get; set; }
    }

    public class TalearrGenreResource
    {
        public string Genre { get; set; }
        public int BookCount { get; set; }
    }

    // TaleArr: book tags under /api/v1/talearr/booktags (Chaptarr tags authors only). The tag
    // labels are Chaptarr's own Settings > Tags; the book links are kept in talearr.db. New
    // routes only, so BookResource stays the shape Readarr clients expect.
    [V1ApiController("talearr/booktags")]
    public class TalearrBookTagsController : Controller
    {
        private readonly ITalearrBookTagService _bookTagService;
        private readonly IBookService _bookService;

        public TalearrBookTagsController(ITalearrBookTagService bookTagService, IBookService bookService)
        {
            _bookTagService = bookTagService;
            _bookService = bookService;
        }

        // Every tagged book. Books without tags are left out.
        [HttpGet]
        public List<TalearrBookTagsResource> GetAll()
        {
            return _bookTagService.GetAll()
                .Where(pair => pair.Value.Any())
                .OrderBy(pair => pair.Key)
                .Select(pair => new TalearrBookTagsResource { BookId = pair.Key, Tags = pair.Value })
                .ToList();
        }

        [HttpGet("{bookId:int}")]
        public ActionResult<TalearrBookTagsResource> Get(int bookId)
        {
            if (_bookService.GetBooks(new[] { bookId }).Count == 0)
            {
                return NotFound();
            }

            return new TalearrBookTagsResource { BookId = bookId, Tags = _bookTagService.Get(bookId) };
        }

        // Replaces the book's tags.
        [HttpPut("{bookId:int}")]
        public ActionResult<TalearrBookTagsResource> Set(int bookId, [FromBody] TalearrBookTagsBody body)
        {
            if (_bookService.GetBooks(new[] { bookId }).Count == 0)
            {
                return NotFound();
            }

            var result = _bookTagService.Apply(new[] { bookId }, body?.Tags, TalearrTagApplyMode.Replace);
            return new TalearrBookTagsResource { BookId = bookId, Tags = result.GetValueOrDefault(bookId) ?? new List<int>() };
        }

        // Bulk add, remove or replace, as the book index editor sends it.
        [HttpPut("editor")]
        public List<TalearrBookTagsResource> SetMany([FromBody] TalearrBookTagsEditorBody body)
        {
            var known = _bookService.GetBooks((body?.BookIds ?? new List<int>()).Distinct().ToList())
                .Select(b => b.Id)
                .ToList();

            var mode = body?.ApplyTags switch
            {
                ApplyTags.Remove => TalearrTagApplyMode.Remove,
                ApplyTags.Replace => TalearrTagApplyMode.Replace,
                _ => TalearrTagApplyMode.Add
            };

            return _bookTagService.Apply(known, body?.Tags, mode)
                .OrderBy(pair => pair.Key)
                .Select(pair => new TalearrBookTagsResource { BookId = pair.Key, Tags = pair.Value })
                .ToList();
        }
    }

    // TaleArr: the genres the metadata server gave the books in the library, for the metadata
    // profile genre lists and the book filters.
    [V1ApiController("talearr/genres")]
    public class TalearrGenresController : Controller
    {
        private readonly IBookService _bookService;

        public TalearrGenresController(IBookService bookService)
        {
            _bookService = bookService;
        }

        [HttpGet]
        public List<TalearrGenreResource> GetAll()
        {
            return _bookService.GetAllBooks()
                .SelectMany(b => (b.Genres ?? new List<string>())
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Select(g => g.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                .Select(g => new TalearrGenreResource { Genre = g.First(), BookCount = g.Count() })
                .OrderByDescending(g => g.BookCount)
                .ThenBy(g => g.Genre, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
