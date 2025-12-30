using Azure.Storage.Queues;
using BKBot.Applications.Models;
using BKBot.Applications.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace BKBot.Function
{
    public class MessageProducer
    {
        private readonly ILogger<MessageProducer> _logger;
        private readonly ChatSessionService _chatSessionService;
        private readonly QueueClient _queueClient;
        private readonly EvolutionService _evolutionService;

        // Debounce window to allow users to finish typing before processing triggers
        private const int DebounceSeconds = 4;

        // Safeguard to protect Queue and Redis payloads
        private const int MAX_INFRA_CHAR_LIMIT = 5000;

        // An Evolution image/video JSON file is typically 5kb to 50kb.
        // We'll set it to 200KB (200 * 1024 bytes) to ensure media passes through the parser,
        // but we block DoS attacks (e.g., 10MB payloads).
        private const long MAX_PAYLOAD_BYTES = 200 * 1024;

        public MessageProducer(ILogger<MessageProducer> logger, ChatSessionService chatSessionService, QueueClient queueClient, EvolutionService evolutionService)
        {
            _logger = logger;
            _chatSessionService = chatSessionService;
            _queueClient = queueClient;
            _evolutionService = evolutionService;
        }

        /// <summary>
        /// Webhook entry point for Evolution API. 
        /// Ingests messages, validates payload size, buffers content in Redis, and schedules a processing trigger.
        /// </summary>
        [Function("IngestMessage")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "message")] HttpRequestData req,
            FunctionContext executionContext)
        {
            var log = executionContext.GetLogger("IngestMessage");
            // If this code is used in production, it is recommended to add authentication validation here.

            var response = req.CreateResponse(HttpStatusCode.OK);

            try
            {
                // Fast-fail: Check Content-Length to reject massive payloads before stream reading
                if (req.Body.Length > MAX_PAYLOAD_BYTES)
                {
                    log.LogWarning("[SECURITY] Payload size exceeded limit. Size: {PayloadSize}", req.Body.Length);
                    return req.CreateResponse(HttpStatusCode.BadRequest);
                }

                await _queueClient.CreateIfNotExistsAsync();

                var messageData = await _evolutionService.ParseWebhookAsync(req.Body);
                if (messageData == null) return response;

                bool isText = messageData.MessageType == "conversation" ||
                              messageData.MessageType == "extendedTextMessage";

                if (!isText)
                {
                    log.LogWarning("[MEDIA FILTER] Rejected Type: {Type} de {Phone}", messageData.MessageType, messageData.Phone);

                    // Mensagem amigável para o usuário
                    await _evolutionService.SendMessageAsync(
                        messageData.Phone,
                        "?? *Ops!* Eu sou uma inteligência artificial focada em texto.\n\nNão consigo ver imagens, ouvir áudios ou assistir vídeos. Por favor, digite sua dúvida em texto para que eu possa te ajudar! ??",
                        log
                    );

                    // Retorna OK e PARA O PROCESSO AQUI. Não vai pra fila nem pro Redis.
                    return response;
                }

                using (log.BeginScope(new Dictionary<string, object> { ["PhoneNumber"] = messageData.Phone }))
                {
                    if (messageData.Text.Length > MAX_INFRA_CHAR_LIMIT)
                    {
                        log.LogWarning("[ABUSE] Text length exceeds technical limit. Chars: {CharCount}", messageData.Text.Length);
                        await _evolutionService.SendMessageAsync(
                            messageData.Phone,
                            "*Mensagem muito longa!* \n\nSeu texto excede o limite técnico de processamento. Por favor, envie mensagens mais curtas.",
                            log
                        );
                        return response;
                    }

                    // Buffers the message in Redis and updates the 'LastActivity' timestamp
                    await _chatSessionService.AddToBufferAsync(messageData.Phone, messageData.Text);

                    // Queue Trigger:
                    // We schedule the message with a visibility timeout equal to the debounce window.
                    // This allows the Consumer to ignore premature triggers if the user is still typing.
                    var queueItem = new MessageQueueItemModel { Phone = messageData.Phone };
                    string jsonMessage = JsonSerializer.Serialize(queueItem);

                    await _queueClient.SendMessageAsync(
                        BinaryData.FromString(jsonMessage),
                        visibilityTimeout: TimeSpan.FromSeconds(DebounceSeconds),
                        timeToLive: TimeSpan.FromMinutes(60)
                    );
                }
                return response;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[WEBHOOK ERROR] Ingestion failed.");
                return response;
            }
        }
    }
}