using BKBot.Applications.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace BKBot.Applications.Services
{
    public class EvolutionService
    {
        private readonly HttpClient _httpClient;

        // Configuration constants
        private const string INSTANCE_NAME = "BKBot";
        private const string BASE_URL = "http://localhost:8080";

        public EvolutionService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task SendMessageAsync(string to, string message, ILogger log)
        {
            try
            {
                string number = to.Replace("whatsapp:", "").Replace("+", "").Trim();
                string url = $"{BASE_URL}/message/sendText/{INSTANCE_NAME}";

                var payload = new
                {
                    number,
                    text = message,
                    options = new { delay = 1000 }
                };

                var jsonContent = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("apikey", Environment.GetEnvironmentVariable("Docker-Evolution"));

                var response = await _httpClient.PostAsync(url, jsonContent);

                if (!response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    log.LogError("[Evolution] Send failed. Status: {StatusCode}, Response: {ResponseBody}", response.StatusCode, responseBody);
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[Evolution] Critical connection error.");
            }
        }

        /// <summary>
        /// Parses the incoming webhook stream and normalizes the WhatsApp data structure.
        /// </summary>
        public async Task<EvolutionWebhookData?> ParseWebhookAsync(Stream requestBodyStream)
        {
            try
            {
                string requestBody = await new StreamReader(requestBodyStream).ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(requestBody)) return null;

                var root = JObject.Parse(requestBody);

                // Event type validation
                var eventType = root["type"]?.ToString() ?? root["event"]?.ToString();
                if (eventType != "messages.upsert") return null;

                var dataToken = root["data"];
                var msgData = (dataToken is JArray array) ? array.FirstOrDefault() : dataToken;

                if (msgData == null) return null;

                // Filter loop: Ignore messages sent by the bot itself
                bool fromMe = (bool?)msgData["key"]?["fromMe"] ?? false;
                if (fromMe) return null;

                // Normalize Phone Number (handle Jid variations)
                string remoteJid = msgData["key"]?["remoteJid"]?.ToString();
                string remoteJidAlt = msgData["key"]?["remoteJidAlt"]?.ToString();
                string? targetJid = (!string.IsNullOrEmpty(remoteJidAlt) && remoteJidAlt.Contains("@s.whatsapp.net"))
                                    ? remoteJidAlt : remoteJid;

                string? phone = targetJid?.Replace("@s.whatsapp.net", "").Replace("@lid", "");

                if (string.IsNullOrWhiteSpace(phone)) return null;

                // Extract text from various message types (conversation, extended, media caption)
                var messageNode = msgData["message"];
                string text = messageNode?["conversation"]?.ToString() ??
                              messageNode?["extendedTextMessage"]?["text"]?.ToString() ??
                              messageNode?["imageMessage"]?["caption"]?.ToString() ??
                              messageNode?["videoMessage"]?["caption"]?.ToString() ??
                              "";

                if (string.IsNullOrWhiteSpace(text)) return null;

                return new EvolutionWebhookData
                {
                    Phone = phone,
                    Text = text
                };
            }
            catch
            {
                // Silently fail on malformed JSON to avoid log flooding from random web crawlers
                return null;
            }
        }
    }
}