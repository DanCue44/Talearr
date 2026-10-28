using System.Collections.Specialized;
using NUnit.Framework;
using NzbDrone.Core.Notifications.CustomScript;

namespace Chaptarr.Core.Test.Talearr.Notifications
{
    [TestFixture]
    public class CustomScriptAliasFixture
    {
        [Test]
        public void every_chaptarr_variable_is_also_passed_as_talearr_and_readarr()
        {
            var env = new StringDictionary();
            env.Add("Chaptarr_EventType", "Download");
            env.Add("Chaptarr_Book_Id", "42");
            env.Add("Other_Value", "kept");

            CustomScript.AddAliasEnvironmentVariables(env);

            // StringDictionary stores names in lower case; that is what the script receives
            Assert.That(env["talearr_eventtype"], Is.EqualTo("Download"));
            Assert.That(env["readarr_eventtype"], Is.EqualTo("Download"));
            Assert.That(env["talearr_book_id"], Is.EqualTo("42"));
            Assert.That(env["readarr_book_id"], Is.EqualTo("42"));
            Assert.That(env["chaptarr_eventtype"], Is.EqualTo("Download"));
            Assert.That(env.ContainsKey("talearr_value"), Is.False);
            Assert.That(env.Count, Is.EqualTo(7));
        }

        [Test]
        public void existing_alias_values_are_not_overwritten()
        {
            var env = new StringDictionary();
            env.Add("Chaptarr_EventType", "Download");
            env.Add("Talearr_EventType", "Custom");

            CustomScript.AddAliasEnvironmentVariables(env);

            Assert.That(env["talearr_eventtype"], Is.EqualTo("Custom"));
            Assert.That(env["readarr_eventtype"], Is.EqualTo("Download"));
        }

        [Test]
        public void empty_or_missing_dictionaries_are_ignored()
        {
            CustomScript.AddAliasEnvironmentVariables(null);

            var env = new StringDictionary();
            CustomScript.AddAliasEnvironmentVariables(env);
            Assert.That(env.Count, Is.EqualTo(0));
        }
    }
}
