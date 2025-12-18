using BKBot.Applications.Models;
using Microsoft.Azure.Functions.Worker;
using System.ClientModel;
using Microsoft.Extensions.Logging;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using BKBot.Applications.Services;
using Azure;
using StackExchange.Redis;
using Azure.Storage.Queues;
using System.Text.Json;

namespace BKBot.Function
{
    public class MessageConsumer
    {
        private readonly EvolutionService _evolutionService;
        private readonly AzureOpenAIClient _openAIClient;
        private readonly ChatHistoryService _chatHistoryService;
        private readonly BufferService _bufferService;
        private readonly IDatabase _redisDb;
        private readonly QueueClient _queueClient;

        // Safety margin slightly lower than producer's debounce to account for latency
        private readonly TimeSpan _debounceThreshold = TimeSpan.FromSeconds(4.8);

        public MessageConsumer(
            EvolutionService evolutionService,
            AzureOpenAIClient openAIClient,
            ChatHistoryService chatHistoryService,
            BufferService bufferService,
            IConnectionMultiplexer redisConnection,
            QueueClient queueClient)
        {
            _evolutionService = evolutionService;
            _openAIClient = openAIClient;
            _chatHistoryService = chatHistoryService;
            _bufferService = bufferService;
            _redisDb = redisConnection.GetDatabase();
            _queueClient = queueClient;
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

            using (log.BeginScope(new Dictionary<string, object> { ["PhoneNumber"] = phone }))
            {
                //DEBOUNCE CHECK
                // If the user has typed something new recently (LastActivity < Threshold),
                // we discard this specific trigger and wait for the subsequent one.
                TimeSpan timeSinceLastMsg = await _bufferService.GetTimeSinceLastActivityAsync(phone);
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
                    if (await _bufferService.IsRateLimitedAsync(phone))
                    {
                        log.LogWarning("[RATE LIMIT] Daily quota reached.");
                        await _bufferService.GetAndClearBufferAsync(phone); // Flush buffer

                        await _evolutionService.SendMessageAsync(
                            phone,
                            "*Daily Limit Reached*\n\nYou have reached the limit of 20 messages. Please try again in 24 hours.",
                            log
                        );
                        return;
                    }

                    // Retrieve and flush the aggregated message buffer
                    string consolidatedText = await _bufferService.GetAndClearBufferAsync(phone);

                    // Handle race condition where buffer might be empty due to parallel execution
                    if (string.IsNullOrWhiteSpace(consolidatedText)) return;

                    // Indicate to user that the bot is composing a reply
                    await _evolutionService.SetPresenceAsync(phone, "composing", log);

                    const int OPENAI_CHAR_LIMIT = 1000;
                    if (consolidatedText.Length > OPENAI_CHAR_LIMIT)
                    {
                        log.LogWarning("[VALIDATION] Message length exceeds OpenAI policy. Size: {MsgSize}", consolidatedText.Length);
                        await _evolutionService.SendMessageAsync(
                            phone,
                            "*Message too long.* Please summarize your query.",
                            log
                        );
                        return;
                    }

                    var chatHistory = await _chatHistoryService.GetHistoryAsync(phone);

                    string responseText = await GetOpenAIResponse(consolidatedText, chatHistory);

                    await _evolutionService.SendMessageAsync(phone, responseText, log);
                    await _chatHistoryService.SaveInteractionAsync(phone, consolidatedText, responseText);
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

        private async Task<string> GetOpenAIResponse(string userQuery, List<ChatMessage> history)
        {
            string deployment = "gpt-35-turbo-Portfolio";
            ChatClient chatClient = _openAIClient.GetChatClient(deployment);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("Simply return the number of characters the message has.")
            };

            messages.AddRange(history);
            messages.Add(new UserChatMessage(userQuery));

            var completionOptions = new ChatCompletionOptions
            {
                Temperature = 0.06f,
                MaxOutputTokenCount = 700,
                FrequencyPenalty = 0.5f,
                PresencePenalty = 0.6f,
            };

            ChatCompletion completion = await chatClient.CompleteChatAsync(messages, completionOptions);
            return completion.Content[0].Text;
        }
    }
}