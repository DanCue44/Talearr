namespace NzbDrone.Core.Download
{
    // Talearr: download clients with a category for comics and manga (Comics).
    public interface IComicsCategorySettings
    {
        string ComicsCategory { get; }
        string EbookCategory { get; }
    }
}
