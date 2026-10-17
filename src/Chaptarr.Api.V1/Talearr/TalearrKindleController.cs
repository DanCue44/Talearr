using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.EbookDelivery;

namespace Chaptarr.Api.V1.Talearr
{
    // TaleArr: Send to Kindle for one book. New routes only.
    //   GET  /api/v1/talearr/kindle/book/{bookId}   whether the book can be sent, and which file
    //   POST /api/v1/talearr/kindle/send/{bookId}   email the best accepted ebook file
    [V1ApiController("talearr/kindle")]
    public class TalearrKindleController : Controller
    {
        private readonly ITalearrKindleService _kindleService;

        public TalearrKindleController(ITalearrKindleService kindleService)
        {
            _kindleService = kindleService;
        }

        [HttpGet("book/{bookId:int}")]
        public TalearrKindleBookStatus GetBookStatus(int bookId)
        {
            return _kindleService.GetBookStatus(bookId);
        }

        [HttpPost("send/{bookId:int}")]
        public TalearrKindleResult Send(int bookId)
        {
            return _kindleService.SendBook(bookId);
        }
    }
}
