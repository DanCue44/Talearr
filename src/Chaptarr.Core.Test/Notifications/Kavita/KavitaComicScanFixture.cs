using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Notifications.Kavita;

namespace Chaptarr.Core.Test.Notifications.Kavita
{
    [TestFixture]
    public class KavitaComicScanFixture
    {
        private FakeProxy _proxy;
        private KavitaService _service;
        private KavitaSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _proxy = new FakeProxy();
            _settings = new KavitaSettings { Host = "Kavita", Port = 5000, ApiKey = "key" };
            _service = new KavitaService(_proxy, LogManager.GetLogger("KavitaTest"));
        }

        private static KavitaLibrary Library(int id, string folder, DateTime lastScanned)
        {
            return new KavitaLibrary { Id = id, Name = folder.Trim('/'), Folders = new List<string> { folder }, LastScanned = lastScanned };
        }

        [Test]
        public void should_scan_the_whole_library_for_a_new_series_folder()
        {
            _proxy.Libraries.Add(Library(1, "/comics", DateTime.UtcNow));
            _proxy.Libraries.Add(Library(2, "/manga", DateTime.UtcNow));

            _service.ScanComicFolder(_settings, "/manga/Witch Hat Atelier", true);

            Assert.That(_proxy.ScannedLibraries, Is.EqualTo(new[] { 2 }));
            Assert.That(_proxy.ScannedFolders, Is.Empty);
        }

        [Test]
        public void should_scan_the_whole_library_when_it_was_never_scanned()
        {
            _proxy.Libraries.Add(Library(2, "/manga", new DateTime(1, 1, 1)));

            _service.ScanComicFolder(_settings, "/manga/Witch Hat Atelier", false);

            Assert.That(_proxy.ScannedLibraries, Is.EqualTo(new[] { 2 }));
        }

        [Test]
        public void should_scan_only_the_folder_of_a_known_series()
        {
            _proxy.Libraries.Add(Library(2, "/manga", DateTime.UtcNow));

            _service.ScanComicFolder(_settings, "/manga/Witch Hat Atelier", false);

            Assert.That(_proxy.ScannedFolders, Is.EqualTo(new[] { "/manga/Witch Hat Atelier" }));
            Assert.That(_proxy.ScannedLibraries, Is.Empty);
        }

        [Test]
        public void should_fall_back_to_a_folder_scan_when_no_library_holds_the_folder()
        {
            _proxy.Libraries.Add(Library(1, "/comics", DateTime.UtcNow));

            _service.ScanComicFolder(_settings, "/manga/Witch Hat Atelier", true);

            Assert.That(_proxy.ScannedFolders, Is.EqualTo(new[] { "/manga/Witch Hat Atelier" }));
        }

        [Test]
        public void should_fall_back_to_a_folder_scan_when_the_lookup_fails()
        {
            _proxy.FailLookup = true;

            _service.ScanComicFolder(_settings, "/manga/Witch Hat Atelier", true);

            Assert.That(_proxy.ScannedFolders, Is.EqualTo(new[] { "/manga/Witch Hat Atelier" }));
        }

        private sealed class FakeProxy : IKavitaServiceProxy
        {
            public List<KavitaLibrary> Libraries { get; } = new List<KavitaLibrary>();
            public List<int> ScannedLibraries { get; } = new List<int>();
            public List<string> ScannedFolders { get; } = new List<string>();
            public bool FailLookup { get; set; }

            public string GetBaseUrl(KavitaSettings settings, string relativePath = null) => "http://Kavita:5000";

            public void Notify(KavitaSettings settings, string message) => ScannedFolders.Add(message);

            public string GetToken(KavitaSettings settings) => "token";

            public List<KavitaLibrary> GetLibraries(KavitaSettings settings, string token)
            {
                if (FailLookup)
                {
                    throw new InvalidOperationException("401");
                }

                return Libraries;
            }

            public void ScanLibrary(KavitaSettings settings, string token, int libraryId) => ScannedLibraries.Add(libraryId);
        }
    }
}
