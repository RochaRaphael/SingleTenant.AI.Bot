using OpenAI.Chat;
using StackExchange.Redis;
using System.Text.Json;

namespace BKBot.Applications.Services
{
    /// <summary>
    /// Manages conversational context persistence in Redis using a sliding window approach.
    /// </summary>
    public class ChatHistoryService
    {
        private readonly IDatabase _redisDb;
        private const int MaxMessages = 20;
        private readonly TimeSpan _expiration = TimeSpan.FromHours(24);

        public ChatHistoryService(IConnectionMultiplexer connectionMultiplexer)
        {
            _redisDb = connectionMultiplexer.GetDatabase();
        }

        public async Task<List<ChatMessage>> GetHistoryAsync(string phoneNumber)
        {
            string key = $"chat_history:{phoneNumber}";
            var data = await _redisDb.StringGetAsync(key);

            if (data.IsNullOrEmpty)
            {
                return new List<ChatMessage>();
            }

            // Deserialize to DTO intermediary since ChatMessage is abstract
            var historyDtos = JsonSerializer.Deserialize<List<MessageDto>>(data.ToString());

            var chatMessages = new List<ChatMessage>();
            foreach (var item in historyDtos)
            {
                if (item.Role == "user")
                    chatMessages.Add(new UserChatMessage(item.Content));
                else if (item.Role == "assistant")
                    chatMessages.Add(new AssistantChatMessage(item.Content));
            }

            return chatMessages;
        }

        public async Task SaveInteractionAsync(string phoneNumber, string userText, string aiResponse)
        {
            string key = $"chat_history:{phoneNumber}";

            var data = await _redisDb.StringGetAsync(key);
            var history = data.IsNullOrEmpty
                ? new List<MessageDto>()
                : JsonSerializer.Deserialize<List<MessageDto>>(data.ToString());

            history.Add(new MessageDto { Role = "user", Content = userText });
            history.Add(new MessageDto { Role = "assistant", Content = aiResponse });

            // Enforce sliding window (Keep only last N messages)
            if (history.Count > MaxMessages)
            {
                history = history.TakeLast(MaxMessages).ToList();
            }

            await _redisDb.StringSetAsync(key, JsonSerializer.Serialize(history), _expiration);
        }

        private class MessageDto
        {
            public string Role { get; set; }
            public string Content { get; set; }
        }
    }
}