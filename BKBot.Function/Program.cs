using Azure;
using Azure.AI.OpenAI;
using Azure.Storage.Queues;
using BKBot.Applications.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.ClientModel.Primitives;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<EvolutionService>();

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    string connectionString = Environment.GetEnvironmentVariable("RedisConnection")
        ?? throw new InvalidOperationException("Variável 'RedisConnection' não encontrada.");
    return ConnectionMultiplexer.Connect(connectionString);
});

builder.Services.AddScoped<BufferService>();
builder.Services.AddScoped<ChatHistoryService>();


builder.Services.AddSingleton<QueueClient>(sp =>
{
    string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
        ?? throw new InvalidOperationException("Variável 'AzureWebJobsStorage' não encontrada.");


    return new QueueClient(connectionString, "whatsapp-process-queue", new QueueClientOptions
    {
        MessageEncoding = QueueMessageEncoding.Base64
    });
});

builder.Services.AddSingleton(provider =>
{
    string endpoint = Environment.GetEnvironmentVariable("GPT_Endpoint")
        ?? throw new InvalidOperationException("Variável 'GPT_Endpoint' não encontrada.");

    string key = Environment.GetEnvironmentVariable("GPT_Key")
        ?? throw new InvalidOperationException("Variável 'GPT_Key' não encontrada.");

    var options = new AzureOpenAIClientOptions();

    options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);

    return new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
});

builder.Build().Run();