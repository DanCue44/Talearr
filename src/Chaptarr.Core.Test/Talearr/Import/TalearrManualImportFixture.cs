using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Talearr.Import;

namespace Chaptarr.Core.Test.Talearr.Import
{
    [TestFixture]
    public class TalearrMatchConfidenceFixture
    {
        private static MatchProvenance Provenance(string matchedVia, params MatchSignal[] conflicts)
        {
            var provenance = new MatchProvenance { MatchedVia = matchedVia, SelectionSource = "automatic" };
            provenance.SupportingSignals.Add(new MatchSignal { Type = "author", Source = "embedded_tag" });
            provenance.SupportingSignals.Add(new MatchSignal { Type = "title", Source = "embedded_tag" });
            provenance.ConflictingSignals.AddRange(conflicts);
            return provenance;
        }

        private static Dictionary<string, List<string>> Tags(string album)
        {
            return new Dictionary<string, List<string>> { ["ALBUM"] = new List<string> { album } };
        }

        [Test]
        public void should_be_low_when_the_title_is_nowhere_near_the_file()
        {
            // Chaptarr #105: a Titan's Curse folder matched to The Last Olympian.
            var result = TalearrMatchConfidence.Evaluate(
                "The Last Olympian",
                Provenance("staged_field_representation"),
                "/old/Rick Riordan/Percy Jackson and the Titan's Curse (#3)/01 Chapter.mp3",
                Tags("Percy Jackson and the Titan's Curse"));

            Assert.That(result.Level, Is.EqualTo(TalearrMatchConfidence.Low));
            Assert.That(result.Notes, Has.Some.Contains("does not appear"));
        }

        [Test]
        public void should_be_high_when_tags_and_title_agree()
        {
            var result = TalearrMatchConfidence.Evaluate(
                "The Titan's Curse",
                Provenance("staged_field_representation"),
                "/old/Rick Riordan/Percy Jackson and the Titan's Curse (#3)/01 Chapter.mp3",
                Tags("Percy Jackson and the Titans Curse"));

            Assert.That(result.Level, Is.EqualTo(TalearrMatchConfidence.High));
        }

        [Test]
        public void should_be_low_when_the_matcher_saw_conflicting_evidence()
        {
            var result = TalearrMatchConfidence.Evaluate(
                "Shift",
                Provenance("staged_field_representation", new MatchSignal { Type = "series_position", Observed = "2", Expected = "1" }),
                "/old/Hugh Howey/Silo #2 - Shift/Shift.epub",
                null);

            Assert.That(result.Level, Is.EqualTo(TalearrMatchConfidence.Low));
            Assert.That(result.Notes, Has.Some.Contains("series position"));
        }

        [Test]
        public void should_be_high_for_an_identifier_match()
        {
            var provenance = new MatchProvenance { MatchedVia = "provider_identifier" };

            var result = TalearrMatchConfidence.Evaluate("Wool", provenance, "/old/x/9781476733951.epub", null);

            Assert.That(result.Level, Is.EqualTo(TalearrMatchConfidence.High));
            Assert.That(result.Notes[0], Does.Contain("identifier"));
        }

        [Test]
        public void should_be_medium_for_a_fallback_route()
        {
            var result = TalearrMatchConfidence.Evaluate(
                "Wool",
                Provenance("undistinguished_audiobook_edition"),
                "/old/Hugh Howey/Wool/Wool.m4b",
                Tags("Wool"));

            Assert.That(result.Level, Is.EqualTo(TalearrMatchConfidence.Medium));
        }

        [Test]
        public void should_trust_a_user_selection()
        {
            var result = TalearrMatchConfidence.Evaluate("Wool", MatchProvenance.ManualSelection(null, null, null), "/x/y.epub", null);

            Assert.That(result.Level, Is.EqualTo(TalearrMatchConfidence.High));
        }

        [Test]
        public void should_ignore_subtitles_and_stop_words()
        {
            Assert.That(TalearrMatchConfidence.TitleAppears("The Way of Kings: The Stormlight Archive", "/old/Sanderson/Way of Kings/part 1.mp3", null), Is.True);
            Assert.That(TalearrMatchConfidence.TitleAppears("Élantris", "/old/Sanderson/Elantris.epub", null), Is.True);
        }

        [Test]
        public void should_return_nothing_without_a_title()
        {
            Assert.That(TalearrMatchConfidence.Evaluate(null, null, "/a.epub", null), Is.Null);
        }
    }

    [TestFixture]
    public class TalearrManualImportSelectionFixture
    {
        private static List<Edition> Editions()
        {
            return new List<Edition>
            {
                new Edition { Id = 11, ForeignEditionId = "hc:edition:30447586-audiobook", AudibleASIN = "B00ABCDEFG" },
                new Edition { Id = 12, ForeignEditionId = "hc:edition:999-ebook", Isbn13 = "9781476733951" }
            };
        }

        [Test]
        public void should_find_an_edition_by_its_foreign_id()
        {
            // Chaptarr #108: foreignEditionId used to be ignored.
            Assert.That(TalearrManualImportSelection.FindEditionByForeignId(Editions(), "HC:EDITION:30447586-audiobook")?.Id, Is.EqualTo(11));
        }

        [Test]
        public void should_find_an_edition_by_asin_or_isbn()
        {
            Assert.That(TalearrManualImportSelection.FindEditionByForeignId(Editions(), "B00ABCDEFG")?.Id, Is.EqualTo(11));
            Assert.That(TalearrManualImportSelection.FindEditionByForeignId(Editions(), "9781476733951")?.Id, Is.EqualTo(12));
        }

        [Test]
        public void should_find_nothing_for_an_unknown_id()
        {
            Assert.That(TalearrManualImportSelection.FindEditionByForeignId(Editions(), "hc:edition:1"), Is.Null);
            Assert.That(TalearrManualImportSelection.FindEditionByForeignId(Editions(), " "), Is.Null);
        }

        [Test]
        public void missing_edition_reason_should_name_the_field()
        {
            var book = new Book { Title = "Elder Race" };

            Assert.That(TalearrManualImportSelection.MissingEditionReason(book, null), Does.Contain("editionId"));
            Assert.That(TalearrManualImportSelection.MissingEditionReason(book, "hc:edition:1"), Does.Contain("foreignEditionId 'hc:edition:1'"));
        }
    }
}
