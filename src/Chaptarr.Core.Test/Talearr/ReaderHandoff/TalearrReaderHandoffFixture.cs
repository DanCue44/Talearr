using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Chaptarr.Core.Test.Talearr.Audiobook;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Talearr.Audiobook.M4b;
using NzbDrone.Core.Talearr.ReaderHandoff;

namespace Chaptarr.Core.Test.Talearr.ReaderHandoff
{
    public class FakeKepubify : ITalearrKepubify
    {
        public string Executable { get; set; } = "/usr/local/bin/kepubify";
        public string FailWith { get; set; }
        public List<(string Source, string Destination)> Conversions { get; } = new List<(string, string)>();

        public string FindExecutable()
        {
            return Executable;
        }

        public string Convert(string executable, string sourceEpub, string destinationFile)
        {
            Conversions.Add((sourceEpub, destinationFile));

            if (FailWith != null)
            {
                return FailWith;
            }

            File.WriteAllText(destinationFile, "KEPUB:" + File.ReadAllText(sourceEpub));
            return null;
        }
    }

    public class FakeEventAggregator : IEventAggregator
    {
        public List<object> Events { get; } = new List<object>();

        public void PublishEvent<TEvent>(TEvent @event)
            where TEvent : class, IEvent
        {
            Events.Add(@event);
        }
    }

    [TestFixture]
    public class TalearrReaderHandoffFixture
    {
        private string _root;
        private string _library;
        private string _watch;
        private FakeKepubify _kepubify;
        private Dictionary<string, string> _rows;
        private FakeEventAggregator _events;
        private TalearrReaderHandoffSettingsService _settingsService;
        private TalearrReaderHandoffService _service;
        private Author _author;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "talearr-handoff-test-" + Guid.NewGuid().ToString("N"));
            _library = Path.Combine(_root, "library");
            _watch = Path.Combine(_root, "ingest");
            Directory.CreateDirectory(_library);
            Directory.CreateDirectory(_watch);

            _rows = new Dictionary<string, string>();
            var (repository, fake) = Fake<IConfigRepository>.Create();
            fake.On(nameof(IConfigRepository.Get), args => _rows.TryGetValue((string)args[0], out var value) ? new Config { Key = (string)args[0], Value = value } : null)
                .On(nameof(IConfigRepository.Upsert), args =>
                {
                    _rows[(string)args[0]] = (string)args[1];
                    return new Config { Key = (string)args[0], Value = (string)args[1] };
                });

            _events = new FakeEventAggregator();
            _settingsService = new TalearrReaderHandoffSettingsService(repository, _events, LogManager.GetCurrentClassLogger());
            _kepubify = new FakeKepubify();
            _service = new TalearrReaderHandoffService(_settingsService, _kepubify, LogManager.GetCurrentClassLogger());
            _author = new Author { Name = "Brandon: Sanderson?" };
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private TalearrReaderHandoffSettings Settings(bool authorFolder = false, bool kepub = false)
        {
            return new TalearrReaderHandoffSettings { Enabled = true, Path = _watch, AuthorFolder = authorFolder, ConvertToKepub = kepub };
        }

        private BookFile LibraryFile(string name, string content = "book")
        {
            var path = Path.Combine(_library, name);
            File.WriteAllText(path, content);
            return new BookFile { Path = path };
        }

        private string[] WatchFiles()
        {
            return Directory.GetFiles(_watch, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_watch, f))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
        }

        [Test]
        public void settings_are_off_by_default()
        {
            var settings = _settingsService.Get();

            Assert.That(settings.Enabled, Is.False);
            Assert.That(settings.Path, Is.Empty);
            Assert.That(settings.AuthorFolder, Is.False);
            Assert.That(settings.ConvertToKepub, Is.False);
            Assert.That(settings.IsActive, Is.False);
        }

        [Test]
        public void settings_are_stored_as_talearr_config_rows_and_announce_the_change()
        {
            _settingsService.Save(new TalearrReaderHandoffSettings { Enabled = true, Path = " /books/ingest ", AuthorFolder = true, ConvertToKepub = true });

            Assert.That(_rows.Keys.All(k => k.StartsWith("talearr.readerhandoff.", StringComparison.Ordinal)), Is.True);
            Assert.That(_rows["talearr.readerhandoff.path"], Is.EqualTo("/books/ingest"));

            var loaded = _settingsService.Get();
            Assert.That(loaded.Enabled && loaded.AuthorFolder && loaded.ConvertToKepub, Is.True);
            Assert.That(_events.Events.OfType<TalearrReaderHandoffSettingsChangedEvent>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void copies_ebooks_and_keeps_the_library_file()
        {
            var file = LibraryFile("Mistborn.epub", "epub-bytes");

            var written = _service.HandOff(_author, new[] { file }, Settings());

            Assert.That(WatchFiles(), Is.EqualTo(new[] { "Mistborn.epub" }));
            Assert.That(written, Has.Count.EqualTo(1));
            Assert.That(File.ReadAllText(file.Path), Is.EqualTo("epub-bytes"));
            Assert.That(File.ReadAllText(Path.Combine(_watch, "Mistborn.epub")), Is.EqualTo("epub-bytes"));
        }

        [Test]
        public void skips_audiobook_files()
        {
            var audio = LibraryFile("Mistborn.m4b");
            var mp3 = LibraryFile("Part 01.mp3");
            var epub = LibraryFile("Mistborn.epub");

            _service.HandOff(_author, new[] { audio, mp3, epub }, Settings());

            Assert.That(WatchFiles(), Is.EqualTo(new[] { "Mistborn.epub" }));
        }

        [Test]
        public void does_nothing_when_disabled()
        {
            var file = LibraryFile("Mistborn.epub");
            var settings = Settings();
            settings.Enabled = false;

            Assert.That(_service.HandOff(_author, new[] { file }, settings), Is.Empty);
            Assert.That(WatchFiles(), Is.Empty);
        }

        [Test]
        public void author_subfolder_uses_a_cleaned_author_name()
        {
            var file = LibraryFile("Mistborn.epub");

            _service.HandOff(_author, new[] { file }, Settings(authorFolder: true));

            var files = WatchFiles();
            Assert.That(files, Has.Length.EqualTo(1));
            var folder = Path.GetDirectoryName(files[0]);
            Assert.That(folder, Does.StartWith("Brandon"));
            Assert.That(folder, Does.Not.Contain(":").And.Not.Contain("?"));
        }

        [Test]
        public void kepub_conversion_writes_only_the_kepub_copy()
        {
            var file = LibraryFile("Mistborn.epub", "epub-bytes");

            _service.HandOff(_author, new[] { file }, Settings(kepub: true));

            Assert.That(WatchFiles(), Is.EqualTo(new[] { "Mistborn.kepub.epub" }));
            Assert.That(File.ReadAllText(Path.Combine(_watch, "Mistborn.kepub.epub")), Is.EqualTo("KEPUB:epub-bytes"));
            Assert.That(File.ReadAllText(file.Path), Is.EqualTo("epub-bytes"));
            Assert.That(_kepubify.Conversions.Single().Source, Is.EqualTo(file.Path));
        }

        [Test]
        public void kepub_conversion_leaves_other_formats_alone()
        {
            var pdf = LibraryFile("Mistborn.pdf");
            var kepub = LibraryFile("Elantris.kepub.epub");

            _service.HandOff(_author, new[] { pdf, kepub }, Settings(kepub: true));

            Assert.That(WatchFiles(), Is.EqualTo(new[] { "Elantris.kepub.epub", "Mistborn.pdf" }));
            Assert.That(_kepubify.Conversions, Is.Empty);
        }

        [Test]
        public void missing_kepubify_copies_the_epub_unconverted()
        {
            _kepubify.Executable = null;
            var file = LibraryFile("Mistborn.epub");

            _service.HandOff(_author, new[] { file }, Settings(kepub: true));

            Assert.That(WatchFiles(), Is.EqualTo(new[] { "Mistborn.epub" }));
        }

        [Test]
        public void failed_conversion_falls_back_to_the_epub_and_leaves_no_partial_file()
        {
            _kepubify.FailWith = "kepubify exited with code 1";
            var file = LibraryFile("Mistborn.epub");

            _service.HandOff(_author, new[] { file }, Settings(kepub: true));

            Assert.That(WatchFiles(), Is.EqualTo(new[] { "Mistborn.epub" }));
        }

        [Test]
        public void missing_watch_folder_throws_and_writes_nothing()
        {
            var file = LibraryFile("Mistborn.epub");
            var settings = Settings();
            settings.Path = Path.Combine(_root, "nope");

            Assert.Throws<DirectoryNotFoundException>(() => _service.HandOff(_author, new[] { file }, settings));
            Assert.That(Directory.Exists(settings.Path), Is.False);
        }

        [Test]
        public void import_event_from_a_download_hands_off()
        {
            _settingsService.Save(Settings());
            var file = LibraryFile("Mistborn.epub");
            var book = new Book { Id = 1, Title = "Mistborn" };

            _service.HandleAsync(new BookImportedEvent(_author, book, new List<BookFile> { file }, new List<BookFile>(), true, null));

            Assert.That(WatchFiles(), Is.EqualTo(new[] { "Mistborn.epub" }));
        }

        [Test]
        public void import_event_from_a_disk_scan_is_ignored()
        {
            _settingsService.Save(Settings());
            var file = LibraryFile("Mistborn.epub");
            var book = new Book { Id = 1, Title = "Mistborn" };

            _service.HandleAsync(new BookImportedEvent(_author, book, new List<BookFile> { file }, new List<BookFile>(), false, null));

            Assert.That(WatchFiles(), Is.Empty);
        }

        [Test]
        public void test_passes_for_a_writable_folder_and_leaves_it_clean()
        {
            var result = _service.Test(Settings());

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Warnings, Is.Empty);
            Assert.That(WatchFiles(), Is.Empty);
        }

        [Test]
        public void test_fails_for_empty_relative_or_missing_folders()
        {
            Assert.That(_service.Test(new TalearrReaderHandoffSettings { Enabled = true }).Success, Is.False);
            Assert.That(_service.Test(new TalearrReaderHandoffSettings { Enabled = true, Path = "relative/folder" }).Success, Is.False);
            Assert.That(_service.Test(new TalearrReaderHandoffSettings { Enabled = true, Path = Path.Combine(_root, "nope") }).Success, Is.False);
        }

        [Test]
        public void test_warns_when_kepub_is_on_without_kepubify()
        {
            _kepubify.Executable = null;

            var result = _service.Test(Settings(kepub: true));

            Assert.That(result.Success, Is.True);
            Assert.That(result.Warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void health_message_only_when_enabled_and_broken()
        {
            Assert.That(TalearrReaderHandoffCheck.GetMessage(TalearrReaderHandoffSettings.Default, _ => false, () => false), Is.Null);
            Assert.That(TalearrReaderHandoffCheck.GetMessage(Settings(), _ => true, () => false), Is.Null);
            Assert.That(TalearrReaderHandoffCheck.GetMessage(Settings(), _ => false, () => true), Does.Contain("does not exist"));
            Assert.That(TalearrReaderHandoffCheck.GetMessage(Settings(kepub: true), _ => true, () => false), Does.Contain("kepubify"));
            Assert.That(TalearrReaderHandoffCheck.GetMessage(Settings(kepub: true), _ => true, () => true), Is.Null);
        }

        [Test]
        public void kepubify_is_found_on_path_or_known_folders()
        {
            var present = new HashSet<string> { "/opt/tools/kepubify" };

            Assert.That(TalearrKepubify.Locate("/usr/sbin:/opt/tools", present.Contains, false), Is.EqualTo("/opt/tools/kepubify"));
            Assert.That(TalearrKepubify.Locate(null, p => p == "/usr/local/bin/kepubify", false), Is.EqualTo("/usr/local/bin/kepubify"));
            Assert.That(TalearrKepubify.Locate("/usr/sbin", _ => false, false), Is.Null);
        }

        [Test]
        public void kepubify_runs_with_an_output_folder_and_copies_the_result()
        {
            var calls = new List<IReadOnlyList<string>>();
            var runner = new FakeRunner(args =>
            {
                calls.Add(args);
                var outFolder = args[1];
                File.WriteAllText(Path.Combine(outFolder, Path.GetFileNameWithoutExtension(args[2]) + ".kepub.epub"), "converted");
                return new M4bProcessResult { ExitCode = 0 };
            });

            var source = LibraryFile("Mistborn.epub", "original");
            var destination = Path.Combine(_watch, "out.kepub.epub");

            var error = new TalearrKepubify(runner, LogManager.GetCurrentClassLogger()).Convert("kepubify", source.Path, destination);

            Assert.That(error, Is.Null);
            Assert.That(calls.Single()[0], Is.EqualTo("-o"));
            Assert.That(calls.Single()[2], Is.EqualTo(source.Path));
            Assert.That(File.ReadAllText(destination), Is.EqualTo("converted"));
            Assert.That(File.ReadAllText(source.Path), Is.EqualTo("original"));
            Assert.That(Directory.Exists(calls.Single()[1]), Is.False);
        }

        [Test]
        public void kepubify_failure_is_reported()
        {
            var runner = new FakeRunner(_ => new M4bProcessResult { ExitCode = 2, StandardError = "bad epub" });
            var source = LibraryFile("Mistborn.epub");

            var error = new TalearrKepubify(runner, LogManager.GetCurrentClassLogger()).Convert("kepubify", source.Path, Path.Combine(_watch, "x.kepub.epub"));

            Assert.That(error, Does.Contain("code 2").And.Contain("bad epub"));
            Assert.That(WatchFiles(), Is.Empty);
        }

        private class FakeRunner : IM4bProcessRunner
        {
            private readonly Func<IReadOnlyList<string>, M4bProcessResult> _handler;

            public FakeRunner(Func<IReadOnlyList<string>, M4bProcessResult> handler)
            {
                _handler = handler;
            }

            public M4bProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string> onOutputLine = null, CancellationToken cancellationToken = default)
            {
                return _handler(arguments);
            }
        }
    }
}
