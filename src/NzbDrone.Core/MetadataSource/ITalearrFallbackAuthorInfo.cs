using System;
using System.Collections.Generic;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MetadataSource
{
    // TaleArr: author data from the fallback metadata source (Open Library) for the add path
    // only. Refresh never calls this (see TalearrMetadataFallbackService).
    public interface ITalearrFallbackAuthorInfo
    {
        bool ShouldUseMetadataFallback(string requestedProviderId, Exception primaryError);

        // Null when the fallback is off or has no matching author.
        Author GetFallbackAuthorInfo(string requestedProviderId, string authorNameHint, IReadOnlyDictionary<string, string> requestedWorkTitles = null);
    }
}
