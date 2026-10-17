using System;
using System.Net;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers.MyAnonaMouse;

namespace Chaptarr.Core.Test.Talearr.Mam
{
    [TestFixture]
    public class MamSessionResponseFixture
    {
        private static HttpResponse Response(HttpStatusCode status, string contentType, string content, params string[] cookies)
        {
            var headers = new HttpHeader { ContentType = contentType };
            foreach (var cookie in cookies)
            {
                headers.Add("Set-Cookie", cookie);
            }

            return new HttpResponse(new HttpRequest("https://www.myanonamouse.net/tor/js/loadSearchJSONbasic.php"), headers, content, status);
        }

        [Test]
        public void forbidden_reply_should_be_a_rejected_session()
        {
            var response = Response(HttpStatusCode.Forbidden, "text/html", "Error, you are not signed in <br />");

            Assert.That(MyAnonaMouseSessionDetector.IsSessionRejected(response), Is.True);
            Assert.That(MyAnonaMouseSessionDetector.IsSessionRejected(response, inspectBody: false), Is.True);
        }

        [Test]
        public void json_not_signed_in_error_should_be_a_rejected_session()
        {
            var response = Response(HttpStatusCode.OK, "application/json", "{\"error\":\"Error, you are not signed in\"}");

            Assert.That(MyAnonaMouseSessionDetector.IsSessionRejected(response), Is.True);
        }

        [Test]
        public void redirect_to_login_should_be_a_rejected_session()
        {
            var response = Response(HttpStatusCode.Found, "text/html", string.Empty);
            response.Headers["Location"] = "/login.php?returnto=%2Ftor%2Fjs%2FloadSearchJSONbasic.php";

            Assert.That(MyAnonaMouseSessionDetector.IsSessionRejected(response), Is.True);
        }

        [Test]
        public void normal_search_reply_should_not_be_rejected()
        {
            var response = Response(HttpStatusCode.OK, "application/json", "{\"perpage\":100,\"start\":0,\"data\":[{\"id\":1,\"title\":\"Storm Front\"}],\"total\":1,\"found\":1}");

            Assert.That(MyAnonaMouseSessionDetector.IsSessionRejected(response), Is.False);
        }

        [Test]
        public void no_results_reply_should_not_be_rejected()
        {
            var response = Response(HttpStatusCode.OK, "application/json", "{\"error\":\"Nothing returned, out of 0\",\"found\":0}");

            Assert.That(MyAnonaMouseSessionDetector.IsSessionRejected(response), Is.False);
        }

        [Test]
        public void html_body_should_only_count_when_inspected()
        {
            var response = Response(HttpStatusCode.OK, "text/html", "<a href=\"/login.php\">Log in</a> You are not signed in");

            Assert.That(MyAnonaMouseSessionDetector.IsSessionRejected(response), Is.True);
            Assert.That(MyAnonaMouseSessionDetector.IsSessionRejected(response, inspectBody: false), Is.False);
        }

        [Test]
        public void set_cookie_with_new_mam_id_should_be_a_rotation()
        {
            var response = Response(HttpStatusCode.OK, "application/json", "{}",
                "mam_id=NEWVALUE%3D%3D; expires=Fri, 01-Jan-2100 00:00:00 GMT; path=/; domain=.myanonamouse.net; secure; HttpOnly");

            Assert.That(MyAnonaMouseSessionDetector.TryGetRotatedMamId(response, "OLDVALUE", out var rotated), Is.True);
            Assert.That(rotated, Is.EqualTo("NEWVALUE%3D%3D"));
        }

        [Test]
        public void same_mam_id_should_not_be_a_rotation()
        {
            var response = Response(HttpStatusCode.OK, "application/json", "{}", "mam_id=SAME; path=/");

            Assert.That(MyAnonaMouseSessionDetector.TryGetRotatedMamId(response, "SAME", out _), Is.False);
        }

        [TestCase("mam_id=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; path=/")]
        [TestCase("mam_id=SOMETHING; Max-Age=0; path=/")]
        [TestCase("mam_id=SOMETHING; expires=Thu, 01-Jan-1970 00:00:01 GMT; path=/")]
        [TestCase("mam_id=; path=/")]
        [TestCase("other_cookie=NEWVALUE; path=/")]
        public void deletions_and_other_cookies_should_not_be_a_rotation(string setCookie)
        {
            var response = Response(HttpStatusCode.OK, "application/json", "{}", setCookie);

            Assert.That(MyAnonaMouseSessionDetector.TryGetRotatedMamId(response, "OLDVALUE", out _), Is.False);
        }

        [TestCase(HttpStatusCode.OK, "{\"Success\":true,\"msg\":\"Completed\",\"ip\":\"185.159.157.20\",\"ASN\":209103,\"AS\":\"Proton AG\"}", MyAnonaMouseSeedboxOutcome.Updated)]
        [TestCase(HttpStatusCode.OK, "{\"Success\":true,\"msg\":\"No change\",\"ip\":\"185.159.157.20\",\"ASN\":209103,\"AS\":\"Proton AG\"}", MyAnonaMouseSeedboxOutcome.NoChange)]
        [TestCase((HttpStatusCode)429, "{\"Success\":false,\"msg\":\"Last change too recent\"}", MyAnonaMouseSeedboxOutcome.TooRecent)]
        [TestCase(HttpStatusCode.Forbidden, "{\"Success\":false,\"msg\":\"No Session Cookie\"}", MyAnonaMouseSeedboxOutcome.InvalidSession)]
        [TestCase(HttpStatusCode.Forbidden, "{\"Success\":false,\"msg\":\"Invalid session\"}", MyAnonaMouseSeedboxOutcome.InvalidSession)]
        [TestCase(HttpStatusCode.Forbidden, "{\"Success\":false,\"msg\":\"Incorrect session type - not allowed this function\"}", MyAnonaMouseSeedboxOutcome.NotAllowed)]
        [TestCase(HttpStatusCode.Forbidden, "{\"Success\":false,\"msg\":\"Incorrect session type - non-API session\"}", MyAnonaMouseSeedboxOutcome.NotAllowed)]
        [TestCase(HttpStatusCode.Forbidden, "{\"Success\":false,\"msg\":\"Invalid session - ASN mismatch\"}", MyAnonaMouseSeedboxOutcome.NetworkMismatch)]
        [TestCase(HttpStatusCode.Forbidden, "{\"Success\":false,\"msg\":\"Invalid session - IP mismatch\"}", MyAnonaMouseSeedboxOutcome.NetworkMismatch)]
        [TestCase(HttpStatusCode.Forbidden, "<html>Forbidden</html>", MyAnonaMouseSeedboxOutcome.InvalidSession)]
        [TestCase(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", MyAnonaMouseSeedboxOutcome.Failed)]
        public void dynamic_seedbox_replies_should_classify(HttpStatusCode status, string body, MyAnonaMouseSeedboxOutcome expected)
        {
            var result = MyAnonaMouseDynamicSeedboxParser.Parse(status, body, DateTime.UtcNow);

            Assert.That(result.Outcome, Is.EqualTo(expected));
        }

        [Test]
        public void dynamic_seedbox_reply_should_keep_ip_and_asn()
        {
            var result = MyAnonaMouseDynamicSeedboxParser.Parse(HttpStatusCode.OK,
                "{\"Success\":true,\"msg\":\"Completed\",\"ip\":\"185.159.157.20\",\"ASN\":209103,\"AS\":\"Proton AG\"}",
                DateTime.UtcNow);

            Assert.That(result.Ip, Is.EqualTo("185.159.157.20"));
            Assert.That(result.Asn, Is.EqualTo("209103"));
            Assert.That(result.Message, Is.EqualTo("Completed"));
            Assert.That(result.IsProblem, Is.False);
        }

        [TestCase(MyAnonaMouseSeedboxOutcome.NoChange, false)]
        [TestCase(MyAnonaMouseSeedboxOutcome.Updated, false)]
        [TestCase(MyAnonaMouseSeedboxOutcome.TooRecent, false)]
        [TestCase(MyAnonaMouseSeedboxOutcome.InvalidSession, true)]
        [TestCase(MyAnonaMouseSeedboxOutcome.NetworkMismatch, true)]
        [TestCase(MyAnonaMouseSeedboxOutcome.NotAllowed, true)]
        [TestCase(MyAnonaMouseSeedboxOutcome.Failed, true)]
        public void only_refusals_and_failures_should_be_problems(MyAnonaMouseSeedboxOutcome outcome, bool problem)
        {
            Assert.That(new MyAnonaMouseSeedboxResult { Outcome = outcome }.IsProblem, Is.EqualTo(problem));
        }
    }
}
