using StackExchange.Redis;
using BKBot.Applications.Models;

namespace BKBot.Applications.Services
{
    /// <summary>
    /// Manages the temporary message buffer in Redis to support the Debounce pattern.
    /// </summary>
    public class BufferService
    {
        private readonly IDatabase _redisDb;
        private readonly TimeSpan _bufferExpiration = TimeSpan.FromMinutes(10);

        public BufferService(IConnectionMultiplexer connectionMultiplexer)
        {
            _redisDb = connectionMultiplexer.GetDatabase();
        }

        /// <summary>
        /// Appends text to the user's buffer and updates the last activity timestamp atomically.
        /// </summary>
        public async Task AddToBufferAsync(string phoneNumber, string text)
        {
            // Use Batch execution to minimize round-trips to Redis
            var batch = _redisDb.CreateBatch();

            string listKey = $"msg_buffer:{phoneNumber}";
            string timeKey = $"last_activity:{phoneNumber}";

            var task1 = batch.ListRightPushAsync(listKey, text);
            var task2 = batch.KeyExpireAsync(listKey, _bufferExpiration);

            // Store Ticks for high-precision time comparison
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

        /// <summary>
        /// Retrieves all buffered messages, concatenates them, and clears the buffer in a single operation logic.
        /// </summary>
        public async Task<string> GetAndClearBufferAsync(string phoneNumber)
        {
            string listKey = $"msg_buffer:{phoneNumber}";
            string timeKey = $"last_activity:{phoneNumber}";

            RedisValue[] values = await _redisDb.ListRangeAsync(listKey);

            if (values == null || values.Length == 0) return string.Empty;

            await _redisDb.KeyDeleteAsync(new RedisKey[] { listKey, timeKey });

            return string.Join("\n", values.Select(v => v.ToString()));
        }

        /// <summary>
        /// Checks if the user has exceeded the daily message quota using a rolling counter.
        /// </summary>
        public async Task<bool> IsRateLimitedAsync(string phoneNumber)
        {
            string rateKey = $"rate_limit:{phoneNumber}";

            long count = await _redisDb.StringIncrementAsync(rateKey);

            // Set TTL only on first increment
            if (count == 1)
            {
                await _redisDb.KeyExpireAsync(rateKey, TimeSpan.FromHours(24));
            }

            return count > 20;
        }
    }
}