using System;
using System.Collections.Generic;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // Audiobook editions of "The Firm" (book 3401) as the live library has them, read only.
    internal static class FirmEditions
    {
        public const string BookTitle = "The Firm";
        public const string Author = "John Grisham";

        // Monitored, Scott Brick, stored AudibleASIN B002PE36VO is stale in the US store, the
        // current US product B002V5BSLC is in its ASIN list.
        public static Edition ScottBrickUs() => new Edition
        {
            Id = 28701,
            BookId = 3401,
            ForeignEditionId = "hc:edition:30583648-audiobook",
            HardcoverEditionId = "30583648",
            Title = "The Firm",
            Language = "eng",
            Publisher = "Random House Audio",
            ReleaseDate = new DateTime(2009, 9, 16),
            NarratorNames = new List<string> { "Scott Brick" },
            DurationSeconds = 61800,
            AudibleASIN = "B002PE36VO",
            Asin = "0739366203",
            Asins = new List<string> { "0739366203", "B002PE36VO", "B002V5BSLC", "B004FU0XQ2", "B004UZWZ18", "B008QT29CE", "B00FG9FPRI", "B072BTP7BD", "B079VB1PFQ", "B079VH2X33", "B07FB4HFD8", "B0BTYTFG28" },
            ReadingFormatId = 2,
            Monitored = true
        };

        // UK product.
        public static Edition ScottBrickUk() => new Edition
        {
            Id = 28702,
            BookId = 3401,
            ForeignEditionId = "gr:48498385-audiobook",
            Title = "The Firm",
            Language = "eng",
            Publisher = "Hodder & Stoughton",
            ReleaseDate = new DateTime(2014, 9, 25),
            NarratorNames = new List<string> { "Scott Brick" },
            DurationSeconds = 61740,
            AudibleASIN = "B00NIXU8GW",
            Asin = "B00NIXGHQ2",
            Asins = new List<string> { "B00NIXGHQ2", "B00NIXKS5S", "B00NIXU8GW", "B07BC4WMF8" },
            ReadingFormatId = 2
        };

        // The abridged D.W. Moffett reading: no runtime, no ASIN, no abridged marker.
        public static Edition MoffettAbridged() => new Edition
        {
            Id = 28705,
            BookId = 3401,
            ForeignEditionId = "hc:edition:699892-audiobook",
            HardcoverEditionId = "699892",
            Title = "The Firm",
            Language = "eng",
            Publisher = "Random House Audio",
            ReleaseDate = new DateTime(1991, 2, 28),
            NarratorNames = new List<string> { "D.W. Moffett" },
            DurationSeconds = 0,
            Asins = new List<string>(),
            ReadingFormatId = 2
        };

        // "The Firm (abridged)", language unknown, nothing else.
        public static Edition TitledAbridged() => new Edition
        {
            Id = 28750,
            BookId = 3401,
            ForeignEditionId = "gr:134610787-audiobook",
            Title = "The Firm (abridged)",
            Language = "und",
            ReleaseDate = new DateTime(1990, 1, 1),
            NarratorNames = new List<string>(),
            DurationSeconds = 0,
            Asins = new List<string>(),
            ReadingFormatId = 2
        };

        public static List<Edition> All() => new List<Edition> { ScottBrickUs(), ScottBrickUk(), MoffettAbridged(), TitledAbridged() };
    }
}
