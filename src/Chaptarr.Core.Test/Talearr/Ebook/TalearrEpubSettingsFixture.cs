using System.Collections.Generic;
using Chaptarr.Core.Test.Talearr.Audiobook;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Talearr.Ebook.Epub;

namespace Chaptarr.Core.Test.Talearr.Ebook
{
    [TestFixture]
    public class TalearrEpubSettingsFixture
    {
        private Dictionary<string, string> _rows;
        private TalearrEpubSettingsService _service;

        [SetUp]
        public void SetUp()
        {
            _rows = new Dictionary<string, string>();

            var (repository, fake) = Fake<IConfigRepository>.Create();
            fake.On(nameof(IConfigRepository.Get), args => _rows.TryGetValue((string)args[0], out var value) ? new Config { Key = (string)args[0], Value = value } : null)
                .On(nameof(IConfigRepository.Upsert), args =>
                {
                    _rows[(string)args[0]] = (string)args[1];
                    return new Config { Key = (string)args[0], Value = (string)args[1] };
                });

            _service = new TalearrEpubSettingsService(repository, LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void conversion_is_off_and_the_original_is_kept_by_default()
        {
            var settings = _service.Get();

            Assert.That(settings.ConvertAfterImport, Is.False);
            Assert.That(settings.KeepOriginal, Is.True);
        }

        [Test]
        public void settings_are_stored_as_talearr_config_rows()
        {
            _service.Save(new TalearrEpubSettings { ConvertAfterImport = true, KeepOriginal = false });

            Assert.That(_rows[TalearrEpubSettingsService.ConvertAfterImportKey], Is.EqualTo("True"));
            Assert.That(_rows[TalearrEpubSettingsService.KeepOriginalKey], Is.EqualTo("False"));
            Assert.That(_service.Get().KeepOriginal, Is.False);
        }

        [Test]
        public void an_unreadable_value_falls_back_to_the_default()
        {
            _rows[TalearrEpubSettingsService.KeepOriginalKey] = "maybe";

            Assert.That(_service.Get().KeepOriginal, Is.True);
        }
    }
}
