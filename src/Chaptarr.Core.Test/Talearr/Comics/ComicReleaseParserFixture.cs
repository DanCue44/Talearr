using NUnit.Framework;
using NzbDrone.Core.Talearr.Comics;
using NzbDrone.Core.Talearr.Comics.Parsing;

namespace Chaptarr.Core.Test.Talearr.Comics
{
    // Release names below are real indexer titles (Books > Comics, October 2026) or the naming
    // patterns documented for Empire and scene comic releases.
    [TestFixture]
    public class ComicReleaseParserFixture
    {
        [TestCase("Witch Hat Atelier v08 (2021) (Digital) (danke-Empire)", "Witch Hat Atelier", 8, 2021, "danke-Empire")]
        [TestCase("Saga v01 (2012) (Digital) (F) (Asgard-Empire).cbr", "Saga", 1, 2012, "Asgard-Empire")]
        [TestCase("Witch.Hat.Atelier.v11.[2022].[Digital].[Ushi].cbz-xpost", "Witch Hat Atelier", 11, 2022, "Ushi")]
        [TestCase("Witch.Hat.Atelier.v13.2025.Digital.LuCaZ", "Witch Hat Atelier", 13, 2025, "LuCaZ")]
        [TestCase("Spy.x.Family.Vol..11.cbz-xpost", "Spy x Family", 11, null, null)]
        [TestCase("Frieren.-.Beyond.Journey's.End.v12.cbz-xpost", "Frieren - Beyond Journey's End", 12, null, null)]
        [TestCase("VIZ.Media.Jujutsu.Kaisen.Vol.06.Black.Flash.2020.Hybrid.Comic.eBook-BitBook-xpost", "Jujutsu Kaisen", 6, 2020, "BitBook")]
        [TestCase("VIZ.Media.One.Piece.Vol.95.Oden.s.Adventure.2020.Hybrid.Comic.eBook-BitBook-xpost", "One Piece", 95, 2020, "BitBook")]
        [TestCase("DC.Comics.-.Absolute.Batman.Vol.03.Devils.Workshop.2026.HYBRID.COMIC.eBook-21A1", "Absolute Batman", 3, 2026, "21A1")]
        public void should_parse_volumes(string title, string series, int volume, int? year, string group)
        {
            var parsed = ComicReleaseParser.Parse(title);

            Assert.That(parsed.SeriesTitle, Is.EqualTo(series));
            Assert.That(parsed.NumberKind, Is.EqualTo(ComicNumberKind.Volume));
            Assert.That(parsed.Number, Is.EqualTo((decimal)volume));
            Assert.That(parsed.Year, Is.EqualTo(year));
            Assert.That(parsed.Group, Is.EqualTo(group));
        }

        [TestCase("Witch Hat Atelier v08 (2021) (Digital) (danke-Empire)", "digital")]
        [TestCase("VIZ.Media.Jujutsu.Kaisen.Vol.03.2020.Hybrid.Comic.eBook-BitBook-xpost", "digital")]
        [TestCase("Oni.Press.-.Rick.and.Morty.2023.Vol.02.The.Space.Shake.Saga.Part.2.2024.Retail.Comic.eBook-BitBook", "digital")]
        [TestCase("The Walking Dead Compendium 01 (2009) (c2c) (GreenGiant-DCP)", "c2c")]
        [TestCase("Absolute.Batman.027.(2025).(digital-mobile).(Son.of.Ultron-Empire).cbr-xpost", "digital")]
        [TestCase("Fate.The.Winx.Saga.v02.The.Shadow.War.(2025).(Graphic.Novel).(Mad.Cave.Studios).(Digital-HD).(webp).(LeDuch).cbz-xpost", "digital")]
        [TestCase("Spy.x.Family.Vol..11.cbz-xpost", "unknown")]
        public void should_read_source(string title, string source)
        {
            Assert.That(ComicReleaseParser.Parse(title).Source, Is.EqualTo(source));
        }

        [TestCase("Chainsaw.Man.Chapter.202.cbz-xpost", 202)]
        [TestCase("Sousou No Frieren Chapter 82.cbz-xpost", 82)]
        [TestCase("One.Piece.Chapter.1148.cbz-xpost", 1148)]
        [TestCase("Kaiju No 8 c101 (2023) (Digital)", 101)]
        public void should_parse_chapters(string title, int chapter)
        {
            var parsed = ComicReleaseParser.Parse(title, new[] { "Kaiju No. 8", "Chainsaw Man", "Sousou no Frieren", "One Piece" });

            Assert.That(parsed.NumberKind, Is.EqualTo(ComicNumberKind.Chapter));
            Assert.That(parsed.Number, Is.EqualTo((decimal)chapter));
        }

        [Test]
        public void should_parse_chapter_ranges_and_language()
        {
            var parsed = ComicReleaseParser.Parse("One.Piece.C1070-C1072.2022-2023.FRENCH.HYBRiD.COMiC.iNTERNAL.CBZ.eBook-TONER-xpost");

            Assert.That(parsed.SeriesTitle, Is.EqualTo("One Piece"));
            Assert.That(parsed.NumberKind, Is.EqualTo(ComicNumberKind.Chapter));
            Assert.That(parsed.Number, Is.EqualTo(1070m));
            Assert.That(parsed.NumberEnd, Is.EqualTo(1072m));
            Assert.That(parsed.Language, Is.EqualTo("fr"));
            Assert.That(parsed.Group, Is.EqualTo("TONER"));
        }

        [TestCase("Jujutsu.Kaisen.211.[2023].[Digital].[anadius].cbz-xpost", "Jujutsu Kaisen", 211)]
        [TestCase("Jujutsu Kaisen 176  2022   Digital .cbz-xpost", "Jujutsu Kaisen", 176)]
        [TestCase("Spy.x.Family.090.[2023].[Digital].[anadius].cbz-xpost", "Spy x Family", 90)]
        [TestCase("Batman.686.(2009).(2.covers).(digital-Empire).cbr-xpost", "Batman", 686)]
        public void should_keep_bare_numbers_for_the_series_to_decide(string title, string series, int number)
        {
            var parsed = ComicReleaseParser.Parse(title);

            Assert.That(parsed.SeriesTitle, Is.EqualTo(series));
            Assert.That(parsed.NumberKind, Is.EqualTo(ComicNumberKind.Bare));
            Assert.That(parsed.Number, Is.EqualTo((decimal)number));
        }

        [Test]
        public void should_parse_volume_packs()
        {
            var parsed = ComicReleaseParser.Parse("Naruto v01-v72 (2003-2015) (Digital) (LuCaZ)");

            Assert.That(parsed.SeriesTitle, Is.EqualTo("Naruto"));
            Assert.That(parsed.Number, Is.EqualTo(1m));
            Assert.That(parsed.NumberEnd, Is.EqualTo(72m));
            Assert.That(parsed.IsPack, Is.True);
            Assert.That(parsed.Year, Is.EqualTo(2003));
        }

        [TestCase("Vinland.Saga.v14.(2025).(Omnibus.Edition).(Digital).(XRA-Empire).cbz-xpost", "omnibus")]
        [TestCase("Berserk Deluxe Edition v01 (2019) (Digital) (danke-Empire)", "deluxe")]
        [TestCase("Monster - The Perfect Edition v09 (2020) (Digital) (danke-Empire)", "perfect")]
        [TestCase("Witch Hat Atelier v08 (2021) (Digital) (danke-Empire)", null)]
        public void should_read_editions(string title, string edition)
        {
            Assert.That(ComicReleaseParser.Parse(title).Edition, Is.EqualTo(edition));
        }

        [Test]
        public void should_use_known_titles_that_hold_numbers()
        {
            var parsed = ComicReleaseParser.Parse("Kaiju No. 8 v10 (2024) (Digital) (1r0n)", new[] { "Kaiju No. 8" });

            Assert.That(parsed.SeriesTitle, Is.EqualTo("Kaiju No. 8"));
            Assert.That(parsed.MatchedKnownTitle, Is.True);
            Assert.That(parsed.NumberKind, Is.EqualTo(ComicNumberKind.Volume));
            Assert.That(parsed.Number, Is.EqualTo(10m));

            var mob = ComicReleaseParser.Parse("Mob Psycho 100 v03 (2019) (Digital) (danke-Empire)", new[] { "Mob Psycho 100" });
            Assert.That(mob.Number, Is.EqualTo(3m));
        }

        [Test]
        public void should_match_titles_written_with_a_times_sign()
        {
            var parsed = ComicReleaseParser.Parse("Spy.x.Family.Vol..11.cbz-xpost", new[] { "SPY×FAMILY" });

            Assert.That(parsed.MatchedKnownTitle, Is.True);
            Assert.That(parsed.Number, Is.EqualTo(11m));
        }

        [Test]
        public void should_report_spin_off_words_after_a_known_title()
        {
            var parsed = ComicReleaseParser.Parse("Witch.Hat.Atelier.Kitchen.v04.2024.Digital.LuCaZ", new[] { "Witch Hat Atelier" });

            Assert.That(parsed.ExtraTitleWords, Is.EqualTo("Kitchen"));
        }

        [TestCase("Some Series v01 (2020) (Digital) (Asura Scans)")]
        [TestCase("Some Series c010 (2020) (Scanlation)")]
        [TestCase("Some Series v01 [MangaDex]")]
        public void should_flag_scanlations(string title)
        {
            Assert.That(ComicReleaseParser.Parse(title).IsScanlation, Is.True);
        }

        [Test]
        public void should_flag_adult_fan_works()
        {
            Assert.That(ComicReleaseParser.Parse("(COMIC1☆28).[AXZ.(Aosora)].タチんぼナミを抱けるドスケベ公園!!2.(One.Piece).zip-[N-Z-B]-xpost").IsAdultHint, Is.True);
        }

        [TestCase("Saga v01 (2012) (Digital) (F) (Asgard-Empire).cbr", "cbr", true)]
        [TestCase("Witch.Hat.Atelier.v11.[2022].[Digital].[Ushi].cbz-xpost", "cbz", false)]
        public void should_read_format_and_fixed(string title, string format, bool isFixed)
        {
            var parsed = ComicReleaseParser.Parse(title);

            Assert.That(parsed.Format, Is.EqualTo(format));
            Assert.That(parsed.IsFixed, Is.EqualTo(isFixed));
        }

        [TestCase("SPY×FAMILY", "spy x family")]
        [TestCase("Frieren: Beyond Journey's End", "frieren beyond journeys end")]
        [TestCase("The Walking Dead", "walking dead")]
        [TestCase("Pokémon Adventures", "pokemon adventures")]
        public void should_normalize_titles(string title, string normalized)
        {
            Assert.That(ComicReleaseParser.NormalizeTitle(title), Is.EqualTo(normalized));
        }
    }
}
