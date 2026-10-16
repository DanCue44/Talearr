using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Talearr.Audiobook.M4b;

namespace Chaptarr.Core.Test.Talearr.Audiobook
{
    [TestFixture]
    public class TalearrM4bSettingsFixture
    {
        private Dictionary<string, string> _rows;
        private TalearrM4bSettingsService _service;

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

            _service = new TalearrM4bSettingsService(repository, LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void conversion_after_import_is_off_by_default()
        {
            Assert.That(_service.Get().ConvertAfterImport, Is.False);
        }

        [Test]
        public void setting_is_stored_as_a_talearr_config_row()
        {
            _service.Save(new TalearrM4bSettings { ConvertAfterImport = true });

            Assert.That(_rows, Is.EqualTo(new Dictionary<string, string> { ["talearr.audiobook.m4bafterimport"] = "True" }));
            Assert.That(_service.Get().ConvertAfterImport, Is.True);
        }

        [Test]
        public void unreadable_values_fall_back_to_the_default()
        {
            _rows["talearr.audiobook.m4bafterimport"] = "maybe";

            Assert.That(_service.Get().ConvertAfterImport, Is.False);
        }
    }
}
