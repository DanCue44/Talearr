using System;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Notifications.Pushover
{
    public interface IPushoverProxy
    {
        void SendNotification(string title, string message, PushoverSettings settings);

        // Talearr: same message with the book cover attached when it can be fetched.
        void SendNotification(string title, string message, string imageUrl, PushoverSettings settings);
        ValidationFailure Test(PushoverSettings settings);
    }

    public class PushoverProxy : IPushoverProxy
    {
        private const string URL = "https://api.pushover.net/1/messages.json";

        // Pushover rejects attachments over 5 MB; covers are far smaller, so stay well under.
        private const int MaxAttachmentBytes = 2 * 1024 * 1024;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public PushoverProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public void SendNotification(string title, string message, PushoverSettings settings)
        {
            SendNotification(title, message, null, settings);
        }

        public void SendNotification(string title, string message, string imageUrl, PushoverSettings settings)
        {
            var requestBuilder = new HttpRequestBuilder(URL).Post();

            requestBuilder.AddFormParameter("token", settings.ApiKey)
                          .AddFormParameter("user", settings.UserKey)
                          .AddFormParameter("device", string.Join(",", settings.Devices))
                          .AddFormParameter("title", title)
                          .AddFormParameter("message", message)
                          .AddFormParameter("priority", settings.Priority);

            if ((PushoverPriority)settings.Priority == PushoverPriority.Emergency)
            {
                requestBuilder.AddFormParameter("retry", settings.Retry);
                requestBuilder.AddFormParameter("expire", settings.Expire);
            }

            if (!settings.Sound.IsNullOrWhiteSpace())
            {
                requestBuilder.AddFormParameter("sound", settings.Sound);
            }

            // Talearr: a cover that cannot be fetched never blocks the notification.
            var cover = FetchCover(imageUrl);

            if (cover != null)
            {
                requestBuilder.AddFormUpload("attachment", "cover" + cover.Item2, cover.Item1, cover.Item3);
            }

            var request = requestBuilder.Build();

            _httpClient.Post(request);
        }

        private Tuple<byte[], string, string> FetchCover(string imageUrl)
        {
            if (imageUrl.IsNullOrWhiteSpace())
            {
                return null;
            }

            try
            {
                var response = _httpClient.Get(new HttpRequest(imageUrl));
                var data = response.ResponseData;
                var contentType = response.Headers.ContentType ?? string.Empty;

                if (data == null || data.Length == 0 || data.Length > MaxAttachmentBytes || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var extension = contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
                return Tuple.Create(data, extension, contentType.Split(';')[0].Trim());
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to fetch cover {0} for Pushover, sending without it", imageUrl);
                return null;
            }
        }

        public ValidationFailure Test(PushoverSettings settings)
        {
            try
            {
                const string title = "Test Notification";
                const string body = "This is a test message from Talearr";

                SendNotification(title, body, settings);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to send test message");
                return new ValidationFailure("ApiKey", "Unable to send test message");
            }

            return null;
        }
    }
}
