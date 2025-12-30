
namespace BKBot.Applications.Services.LLMServices
{
    public interface ILLMService
    {
        Task<string> GetAIResponseAsync(
            string userQuery,
            string? currentState,
            string? lastAssistantResponse);

        Task<string> GenerateNewStateAsync(
            string? previousState,
            string userQuery,
            string aiResponse);
    }

    public class LLMRateLimitException : Exception
    {
        public LLMRateLimitException(string message) : base(message) { }
    }
}
