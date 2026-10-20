using Equ;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class Ratings : MemberwiseEquatable<Ratings>, IEmbeddedDocument
    {
        public int Votes { get; set; }
        public decimal Value { get; set; }

        // Talearr: readers who shelved the book (Hardcover users_count). Hardcover has far fewer
        // ratings than Goodreads, so a book with readers but no ratings would otherwise read as
        // having no popularity. Stored inside the Ratings JSON, so no schema change; 0 for rows
        // from the metadata server.
        public int Readers { get; set; }

        public double Popularity => (double)Value * Votes;
    }
}
