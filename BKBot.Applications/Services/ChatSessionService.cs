using StackExchange.Redis;
using BKBot.Applications.Models;

namespace BKBot.Applications.Services
{
    public class ChatSessionService
    {
        private readonly IDatabase _redisDb;
        private readonly TimeSpan _bufferExpiration = TimeSpan.FromMinutes(10);
        private readonly TimeSpan _expiration = TimeSpan.FromHours(24);
        private readonly int _dailyMessageLimit = 20;

        public ChatSessionService(IConnectionMultiplexer connectionMultiplexer)
        {
            _redisDb = connectionMultiplexer.GetDatabase();
        }

        public async Task AddToBufferAsync(string phoneNumber, string text)
        {
            var batch = _redisDb.CreateBatch();

            string listKey = $"msg_buffer:{phoneNumber}";
            string timeKey = $"last_activity:{phoneNumber}";

            var task1 = batch.ListRightPushAsync(listKey, text);
            var task2 = batch.KeyExpireAsync(listKey, _bufferExpiration);

            var task3 = batch.StringSetAsync(timeKey, DateTime.UtcNow.Ticks);
            var task4 = batch.KeyExpireAsync(timeKey, _bufferExpiration);

            batch.Execute();
            await Task.WhenAll(task1, task2, task3, task4);
        }

        public async Task<TimeSpan> GetTimeSinceLastActivityAsync(string phoneNumber)
        {
            string timeKey = $"last_activity:{phoneNumber}";
            var value = await _redisDb.StringGetAsync(timeKey);

            if (value.IsNullOrEmpty) return TimeSpan.MaxValue;

            long lastTicks = (long)value;
            var lastActivity = new DateTime(lastTicks, DateTimeKind.Utc);

            return DateTime.UtcNow - lastActivity;
        }

        public async Task<string> GetAndClearBufferAsync(string phoneNumber)
        {
            string listKey = $"msg_buffer:{phoneNumber}";
            string timeKey = $"last_activity:{phoneNumber}";

            RedisValue[] values = await _redisDb.ListRangeAsync(listKey);

            if (values == null || values.Length == 0) return string.Empty;

            await _redisDb.KeyDeleteAsync(new RedisKey[] { listKey, timeKey });

            return string.Join("\n", values.Select(v => v.ToString()));
        }
        public async Task<bool> IsRateLimitedAsync(string phoneNumber)
        {
            string rateKey = $"rate_limit:{phoneNumber}";

            long count = await _redisDb.StringIncrementAsync(rateKey);
            if (count == 1)
            {
                await _redisDb.KeyExpireAsync(rateKey, TimeSpan.FromHours(24));
            }

            return count > _dailyMessageLimit;
        }

        public async Task<string?> GetStateAsync(string phoneNumber)
        {
            string key = $"chat_state:{phoneNumber}";
            var data = await _redisDb.StringGetAsync(key);

            return data.IsNullOrEmpty ? null : data.ToString();
        }

        public async Task SaveStateAsync(string phoneNumber, string newState)
        {
            if (string.IsNullOrWhiteSpace(newState)) return;

            string key = $"chat_state:{phoneNumber}";
            await _redisDb.StringSetAsync(key, newState, _expiration);
        }

        public async Task SaveLastBotResponseAsync(string phoneNumber, string response)
        {
            string key = $"last_bot_msg:{phoneNumber}";
            await _redisDb.StringSetAsync(key, response, _expiration);
        }

        public async Task<string?> GetLastBotResponseAsync(string phoneNumber)
        {
            string key = $"last_bot_msg:{phoneNumber}";
            var val = await _redisDb.StringGetAsync(key);
            return val.IsNullOrEmpty ? null : val.ToString();
        }
    }
}