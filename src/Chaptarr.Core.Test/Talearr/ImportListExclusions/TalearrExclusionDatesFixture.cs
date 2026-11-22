using System;
using System.Collections.Generic;
using Chaptarr.Core.Test.Talearr.Recordings;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Talearr.ImportListExclusions;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.ImportListExclusions
{
    // Talearr: import list exclusion added dates live in talearr.db, never in chaptarr.db.
    [TestFixture]
    public class TalearrExclusionDatesFixture
    {
        private TalearrTestFolder _folder;
        private TalearrDatabase _database;
        private TalearrExclusionDateStore _store;
        private List<ImportListExclusion> _rows;
        private ImportListExclusionService _service;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _database = _folder.CreateDatabase();
            _store = new TalearrExclusionDateStore(_database, LogManager.GetLogger("TalearrTest"));
            _rows = new List<ImportListExclusion>();

            var (repository, fake) = InterfaceFake<IImportListExclusionRepository>.Create();
            fake.On(nameof(IImportListExclusionRepository.Insert), args =>
            {
                var row = (ImportListExclusion)args[0];
                row.Id = _rows.Count + 1;
                _rows.Add(row);
                return row;
            });

            _service = new ImportListExclusionService(repository, LogManager.GetLogger("TalearrTest"), _store);
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        [Test]
        public void schema_adds_the_exclusion_dates_table()
        {
            using var connection = _database.OpenConnection();

            Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ImportListExclusionDates'"), Is.EqualTo(1));
        }

        [Test]
        public void adding_an_exclusion_records_when_it_was_added()
        {
            var before = DateTime.UtcNow.AddSeconds(-1);
            var added = _service.Add(new ImportListExclusion { ForeignId = "hc:42", Name = "Author" });

            var dates = _store.GetAddedDates(new[] { added });

            Assert.That(dates.ContainsKey(added.Id), Is.True);
            Assert.That(dates[added.Id], Is.GreaterThanOrEqualTo(before).And.LessThanOrEqualTo(DateTime.UtcNow.AddSeconds(1)));
        }

        [Test]
        public void older_exclusions_have_no_date()
        {
            var old = new ImportListExclusion { Id = 7, ForeignId = "hc:7", Name = "Old" };

            Assert.That(_store.GetAddedDates(new[] { old }), Is.Empty);
        }

        [Test]
        public void dates_are_kept_per_media_scope()
        {
            _store.RecordAdded(new[] { new ImportListExclusion { Id = 1, ForeignId = "hc:1", MediaType = BookMediaType.Ebook } });

            var ebook = new ImportListExclusion { Id = 1, ForeignId = "hc:1", MediaType = BookMediaType.Ebook };
            var audio = new ImportListExclusion { Id = 2, ForeignId = "hc:1", MediaType = BookMediaType.Audiobook };

            Assert.That(_store.GetAddedDates(new[] { ebook, audio }).Keys, Is.EquivalentTo(new[] { 1 }));
        }

        [Test]
        public void a_broken_talearr_db_only_blanks_the_dates()
        {
            var (database, fake) = InterfaceFake<ITalearrDatabase>.Create();
            fake.On(nameof(ITalearrDatabase.OpenConnection), args => throw new InvalidOperationException("unusable"))
                .On(nameof(ITalearrDatabase.IsCorruption), args => false);

            var store = new TalearrExclusionDateStore(database, LogManager.GetLogger("TalearrTest"));

            Assert.DoesNotThrow(() => store.RecordAdded(new[] { new ImportListExclusion { ForeignId = "hc:1" } }));
            Assert.That(store.GetAddedDates(new[] { new ImportListExclusion { Id = 1, ForeignId = "hc:1" } }), Is.Empty);
        }
    }
}
