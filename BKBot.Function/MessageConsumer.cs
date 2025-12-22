using Azure;
using Azure.AI.OpenAI;
using Azure.Storage.Queues;
using BKBot.Applications.Models;
using BKBot.Applications.Services;
using BKBot.Applications.Services.LLMServices;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using StackExchange.Redis;
using System.ClientModel;
using System.Text.Json;

namespace BKBot.Function
{
    public class MessageConsumer
    {
        private readonly EvolutionService _evolutionService;
        private readonly ChatSessionService _chatSessionService;
        private readonly IDatabase _redisDb;
        private readonly QueueClient _queueClient;
        private readonly ILLMService _llmService;

        // Safety margin slightly lower than producer's debounce to account for latency
        private readonly TimeSpan _debounceThreshold = TimeSpan.FromSeconds(3.8);

        public MessageConsumer(
            EvolutionService evolutionService,
            ChatSessionService chatSessionService,
            IConnectionMultiplexer redisConnection,
            QueueClient queueClient,
            ILLMService llmService)
        {
            _evolutionService = evolutionService;
            _chatSessionService = chatSessionService;
            _redisDb = redisConnection.GetDatabase();
            _queueClient = queueClient;
            _llmService = llmService;
        }

        /// <summary>
        /// Orchestrates the response generation pipeline.
        /// Handles debounce validation, rate limiting, and AI interaction within a distributed lock.
        /// </summary>
        [Function("HandleMessageReply")]
        public async Task Run(
            [QueueTrigger("whatsapp-process-queue", Connection = "AzureWebJobsStorage")] MessageQueueItem queueItem,
            FunctionContext executionContext)
        {
            var log = executionContext.GetLogger("HandleMessageReply");
            string phone = queueItem.Phone;
            string lockKey = $"processing_lock:{phone}";
            const int MAX_CHAR_LIMIT = 1000;

            using (log.BeginScope(new Dictionary<string, object> { ["PhoneNumber"] = phone }))
            {
                //DEBOUNCE CHECK
                // If the user has typed something new recently (LastActivity < Threshold),
                // we discard this specific trigger and wait for the subsequent one.
                TimeSpan timeSinceLastMsg = await _chatSessionService.GetTimeSinceLastActivityAsync(phone);
                if (timeSinceLastMsg < _debounceThreshold)
                    return;

                //DISTRIBUTED LOCK (Idempotency)
                // Ensures only one function instance processes the buffer per user at a time.
                bool isLocked = !await _redisDb.StringSetAsync(lockKey, "LOCKED", TimeSpan.FromMinutes(5), When.NotExists);

                if (isLocked)
                {
                    // Contention detected. Re-queue to try again shortly.
                    await RequeueMessageAsync(queueItem, delaySeconds: 10);
                    return;
                }

                try
                {
                    // Rate Limiting Policy (20 msgs/24h)
                    if (await _chatSessionService.IsRateLimitedAsync(phone))
                    {
                        log.LogWarning("[RATE LIMIT] Daily quota reached.");
                        await _chatSessionService.GetAndClearBufferAsync(phone); // Flush buffer

                        await _evolutionService.SendMessageAsync(
                            phone,
                            "*Daily Limit Reached*\n\nYou have reached the limit of 20 messages. Please try again in 24 hours.",
                            log
                        );
                        return;
                    }

                    // Retrieve and flush the aggregated message buffer
                    string consolidatedText = await _chatSessionService.GetAndClearBufferAsync(phone);

                    // Handle race condition where buffer might be empty due to parallel execution
                    if (string.IsNullOrWhiteSpace(consolidatedText)) return;

                    // Indicate to user that the bot is composing a reply
                    await _evolutionService.SetPresenceAsync(phone, "composing", log);

                    if (consolidatedText.Length > MAX_CHAR_LIMIT)
                    {
                        log.LogWarning("[VALIDATION] Message length exceeds OpenAI policy. Size: {MsgSize}", consolidatedText.Length);
                        await _evolutionService.SendMessageAsync(
                            phone,
                            "*Message too long.* Please summarize your query.",
                            log
                        );
                        return;
                    }

                    string? currentState = await _chatSessionService.GetStateAsync(phone);

                    string responseText = await _llmService.GetAIResponseAsync(consolidatedText, currentState);

                    await _evolutionService.SendMessageAsync(phone, responseText, log);
                    
                    string newState = await _llmService.GenerateNewStateAsync(currentState, consolidatedText, responseText);

                    await _chatSessionService.SaveStateAsync(phone, newState);
                }
                catch (ClientResultException ex) when (ex.Status == 429)
                {
                    log.LogWarning("[OPENAI 429] Rate limit hit. Backing off for 60s.");

                    // Implement exponential backoff by utilizing the queue's visibility timeout
                    await RequeueMessageAsync(queueItem, delaySeconds: 60);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "[CONSUMER ERROR] Processing pipeline failed.");
                    throw; // Rethrow to allow Azure DLQ (Dead Letter Queue) handling
                }
                finally
                {
                    // Always release lock to prevent user starvation
                    await _redisDb.KeyDeleteAsync(lockKey);
                }
            }
        }

        private async Task RequeueMessageAsync(MessageQueueItem item, int delaySeconds)
        {
            string jsonMessage = JsonSerializer.Serialize(item);
            await _queueClient.SendMessageAsync(
                BinaryData.FromString(jsonMessage),
                visibilityTimeout: TimeSpan.FromSeconds(delaySeconds),
                timeToLive: TimeSpan.FromMinutes(60)
            );
        }
    }
}