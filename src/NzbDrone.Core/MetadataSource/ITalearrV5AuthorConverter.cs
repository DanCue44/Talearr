using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.BookInfo.V5;

namespace NzbDrone.Core.MetadataSource
{
    // Talearr: turns a V5 author payload that did not come from the metadata server (Talearr's
    // own Hardcover source, docs/TALEARR_METADATA_SOURCE.md) into domain entities with the exact
    // conversion the server's payloads get. Pure: no HTTP, no database.
    public interface ITalearrV5AuthorConverter
    {
        Author ConvertV5AuthorResponse(V5AuthorResponse response);
    }
}
