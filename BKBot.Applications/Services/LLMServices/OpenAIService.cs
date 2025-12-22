using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace BKBot.Applications.Services.LLMServices
{
    public class OpenAIService : ILLMService
    {
        private readonly AzureOpenAIClient _openAIClient;
        private const string DeploymentName = "gpt-35-turbo-Portfolio";

        public OpenAIService(AzureOpenAIClient openAIClient)
        {
            _openAIClient = openAIClient;
        }

        public async Task<string> GetAIResponseAsync(string userQuery, string? currentState)
        {
            try
            {
                ChatClient chatClient = _openAIClient.GetChatClient(DeploymentName);

                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage("Você é o Luffy do One Piece. Responda de forma divertida e direta.")
                };

                // Só adicionamos a memória se ela realmente existir
                if (!string.IsNullOrWhiteSpace(currentState))
                {
                    messages.Add(new SystemChatMessage($"Contexto da conversa atual: {currentState}"));
                }

                messages.Add(new UserChatMessage(userQuery));

                ChatCompletion completion = await chatClient.CompleteChatAsync(messages);
                return completion.Content[0].Text;
            }
            catch (Exception ex)
            {
                throw new Exception($"Erro na OpenAI: {ex.Message}", ex);
            }
        }

        public async Task<string> GenerateNewStateAsync(string? previousState, string userQuery, string aiResponse)
        {
            try
            {
                ChatClient chatClient = _openAIClient.GetChatClient(DeploymentName);

                // Tratamos o estado anterior caso seja a primeira mensagem
                string context = string.IsNullOrWhiteSpace(previousState)
                    ? "Nenhum (esta é a primeira mensagem da conversa)."
                    : previousState;

                var prompt = $@"
                    Com base no histórico resumido, na pergunta atual e na resposta dada, gere um NOVO resumo curtíssimo.
                    Mantenha nomes, preferências citadas ou decisões tomadas.

                    Histórico de Estados: {context}
                    Última Pergunta: {userQuery}
                    Sua Resposta: {aiResponse}
            
                    Novo Resumo:";

                ChatCompletion completion = await chatClient.CompleteChatAsync(new List<ChatMessage>
                {
                    new SystemChatMessage("Você é um processador de memória para IA. Resuma os pontos chave da conversa em no máximo 2 parágrafos curtos."),
                    new UserChatMessage(prompt)
                });

                return completion.Content[0].Text;
            }
            catch
            {
                // Se falhar e for a primeira vez, retorna string vazia. Se já tinha algo, mantém o que tinha.
                return previousState ?? string.Empty;
            }
        }
    }
}
