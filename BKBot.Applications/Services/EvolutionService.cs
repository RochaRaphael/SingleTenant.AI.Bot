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
        public async Task<EvolutionWebhookDataModel?> ParseWebhookAsync(Stream requestBodyStream)
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
                if ((bool?)msgData["key"]?["fromMe"] ?? false) return null; ;

                // Normalize Phone Number (handle Jid variations)
                string remoteJid = msgData["key"]?["remoteJid"]?.ToString();
                string remoteJidAlt = msgData["key"]?["remoteJidAlt"]?.ToString();
                string? targetJid = (!string.IsNullOrEmpty(remoteJidAlt) && remoteJidAlt.Contains("@s.whatsapp.net"))
                                    ? remoteJidAlt : remoteJid;

                string? phone = targetJid?.Replace("@s.whatsapp.net", "").Replace("@lid", "");
                if (string.IsNullOrWhiteSpace(phone)) return null;

                // Extract text from various message types (conversation, extended, media caption)
                var messageNode = msgData["message"];
                if (messageNode == null) return null;

                // Se for mensagem temporária, o conteúdo real está um nível abaixo
                if (messageNode["ephemeralMessage"] != null)
                {
                    messageNode = messageNode["ephemeralMessage"]?["message"];
                }

                if (messageNode == null) return null;

                string messageType = "unknown";
                string? extractedText = null;

                // Verifica se é TEXTO REAL (Os únicos que importam o conteúdo)
                if (messageNode["conversation"] != null)
                {
                    messageType = "conversation";
                    extractedText = messageNode["conversation"]?.ToString();
                }
                else if (messageNode["extendedTextMessage"] != null)
                {
                    messageType = "extendedTextMessage";
                    extractedText = messageNode["extendedTextMessage"]?["text"]?.ToString();
                }
                // Verifica se é MÍDIA (Apenas identificamos o tipo, ignoramos legenda)
                else if (messageNode["imageMessage"] != null) messageType = "imageMessage";
                else if (messageNode["videoMessage"] != null) messageType = "videoMessage";
                else if (messageNode["audioMessage"] != null) messageType = "audioMessage";
                else if (messageNode["stickerMessage"] != null) messageType = "stickerMessage";
                else if (messageNode["documentMessage"] != null) messageType = "documentMessage";

                // Se for "unknown", ignoramos (ex: protocolMessage, reactionMessage)
                if (messageType == "unknown") return null;

                // Se for texto, retorna o texto. Se for mídia, retorna null ou uma tag visual.
                return new EvolutionWebhookDataModel
                {
                    Phone = phone,
                    MessageType = messageType,
                    Text = extractedText
                };
            }
            catch
            {
                // Silently fail on malformed JSON to avoid log flooding from random web crawlers
                return null;
            }
        }

        public async Task SetPresenceAsync(string to, string presenceType, ILogger log)
        {
            try
            {
                string number = to.Replace("whatsapp:", "").Replace("+", "").Trim();
                string url = $"{BASE_URL}/chat/sendPresence/{INSTANCE_NAME}";

                var payload = new
                {
                    number,
                    presence = presenceType,
                    delay = 8000
                };

                var jsonContent = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("apikey", Environment.GetEnvironmentVariable("Docker-Evolution"));

                var response = await _httpClient.PostAsync(url, jsonContent);

                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("[Evolution] Failed to set presence. Status: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("[Evolution] Error setting presence: {Message}", ex.Message);
            }
        }
    }
}