# BKBot Architecture (WhatsApp Integration)

[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple?logo=dotnet)](https://dotnet.microsoft.com/) [![Azure Functions](https://img.shields.io/badge/Azure%20Functions-Isolated-blue?logo=azurefunctions)](https://azure.microsoft.com/en-us/products/functions/) [![Redis](https://img.shields.io/badge/Redis-Cache-red?logo=redis)](https://redis.io/) [![Azure OpenAI](https://img.shields.io/badge/Azure%20OpenAI-GPT--3.5-green?logo=openai)](https://azure.microsoft.com/services/openai/) [![Evolution API](https://img.shields.io/badge/Evolution%20API-v2-orange?logo=whatsapp)](https://github.com/EvolutionAPI/evolution-api)

**BKBot** is a robust, event-driven architecture designed to handle WhatsApp interactions using **Azure Functions**, **Azure OpenAI**, and **Evolution API**. Unlike simple synchronous bots, this solution solves a common problem in chat interfaces: **"fragmented messaging."** When a user sends multiple short messages in a row (e.g., *"Hi"*, *"I need help"*, *"With my order"*), a standard bot might trigger three separate AI responses.

This architecture implements a **Debounce Pattern** using Redis and Azure Storage Queues. It intelligently buffers incoming messages, waits for the user to finish typing, aggregates the context, and sends a single, coherent response via GPT-3.5. It also features distributed locking for concurrency control and rate limiting to prevent abuse.

## Architecture

The system operates on a **Producer/Consumer** model to ensure high scalability and decoupling between message ingestion and AI processing.

* **Ingestion (Producer Function)** – Receives the Webhook from **Evolution API** and validates payload size. Instead of processing immediately, it buffers the message text in a Redis List and places a signal message in an **Azure Storage Queue** with a visibility timeout (e.g., 5 seconds), acting as the "debounce" timer.
* **Processing (Consumer Function)** – Triggered when the queue message becomes visible. It performs a **Debounce Check** in Redis; if the user typed again recently, it aborts execution.
* **Distributed Lock** – Uses Redis to ensure only one function processes a specific user's chat at a time.
* **AI Generation** – Aggregates all buffered text, retrieves chat history (Sliding Window), and calls **Azure OpenAI** to generate the response.
* **Evolution API** – A Dockerized gateway that connects to WhatsApp Web, handling encryption and socket communication.

## Key Technologies

* **C# (.NET 8 Isolated Worker)** – The core logic running on Azure Functions.
* **Azure Storage Queues** – Handles the asynchronous signaling and visibility timeouts.
* **StackExchange.Redis** – High-performance client for buffering and distributed locking.
* **Azure OpenAI Service** – Utilizing `gpt-35-turbo` for generating human-like responses.
* **Evolution API v2** – Open-source WhatsApp integration API.
* **Docker Compose** – Orchestrates the local environment (Postgres, Redis, Evolution, Azurite).

## Setup

### Prerequisites

* **Docker Desktop** – You need Docker installed and running to spin up the infrastructure.
* **Azure OpenAI Resource** – You need an endpoint and API Key for a deployment (e.g., `gpt-35-turbo`).
* **.NET 8 SDK** – Required to build and run the Azure Functions.
* **Git** – Version control.

### Configuration

1.  **Clone the Repository**:

    ```bash
    git clone [https://github.com/YourUser/BKBot.git](https://github.com/YourUser/BKBot.git)
    cd BKBot
    ```

2.  **Environment Variables**: Create a `local.settings.json` file in your Functions project root (e.g., inside `BKBot.Function/`) with the following content:

    ```json
    {
      "IsEncrypted": false,
      "Values": {
        "AzureWebJobsStorage": "UseDevelopmentStorage=true",
        "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
        "RedisConnection": "localhost:6379",
        "GPT_Endpoint": "[https://YOUR-RESOURCE.openai.azure.com/](https://YOUR-RESOURCE.openai.azure.com/)",
        "GPT_Key": "YOUR-OPENAI-KEY",
        "Docker-Evolution": "42968F67V9DF9V76HDSF98V7HDE10F7D57E11"
      }
    }
    ```

### Running with Docker

This project relies on local infrastructure services. We use Docker Compose to spin them up.

* **Start Infrastructure**: Navigate to the folder containing `docker-compose.yml` and run:

    ```bash
    docker-compose up -d
    ```

    This will start **Evolution API** (Port 8080), **Postgres**, **Redis** (Port 6379), and **Azurite** (Storage Emulator).

* **Configure WhatsApp**:
    1.  Open `http://localhost:8080/manager`.
    2.  Login with the API Key: `42968F67V9DF9V76HDSF98V7HDE10F7D57E11`.
    3.  Create a new instance named **BKBot** and scan the QR Code.
    4.  **Important:** Set the Webhook URL in Evolution API to `http://host.docker.internal:7071/api/message`.

* **Run the .NET Application**:
    In your terminal, navigate to the Functions project directory:

    ```bash
    dotnet restore
    dotnet start
    ```

## Key Code Snippets

* **The Debounce Logic (Producer)**: Buffering the message and scheduling a delayed queue item.

    ```csharp
    // Buffers the message in Redis
    await _bufferService.AddToBufferAsync(messageData.Phone, messageData.Text);

    // Schedules a trigger with delay (DebounceSeconds = 5)
    await _queueClient.SendMessageAsync(
        BinaryData.FromString(jsonMessage),
        visibilityTimeout: TimeSpan.FromSeconds(DebounceSeconds), 
        timeToLive: TimeSpan.FromMinutes(60)
    );
    ```

* **Handling the Buffer (Consumer)**: Checking if we should process or wait.

    ```csharp
    TimeSpan timeSinceLastMsg = await _bufferService.GetTimeSinceLastActivityAsync(phone);

    // If user typed recently (time < threshold), ignore this trigger
    if (timeSinceLastMsg < _debounceThreshold) return; 

    // Retrieve all lines typed by the user as one consolidated block
    string consolidatedText = await _bufferService.GetAndClearBufferAsync(phone);
    ```

---

# Português 
<img src="https://upload.wikimedia.org/wikipedia/commons/0/05/Flag_of_Brazil.svg" width="80" />  <img src="https://upload.wikimedia.org/wikipedia/commons/5/5c/Flag_of_Portugal.svg" width="80" />

# Arquitetura BKBot (Integração WhatsApp)

[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple?logo=dotnet)](https://dotnet.microsoft.com/) [![Azure Functions](https://img.shields.io/badge/Azure%20Functions-Isolated-blue?logo=azurefunctions)](https://azure.microsoft.com/en-us/products/functions/) [![Redis](https://img.shields.io/badge/Redis-Cache-red?logo=redis)](https://redis.io/) [![Azure OpenAI](https://img.shields.io/badge/Azure%20OpenAI-GPT--3.5-green?logo=openai)](https://azure.microsoft.com/services/openai/) [![Evolution API](https://img.shields.io/badge/Evolution%20API-v2-orange?logo=whatsapp)](https://github.com/EvolutionAPI/evolution-api)

**BKBot** é uma arquitetura robusta e orientada a eventos projetada para gerenciar interações no WhatsApp usando **Azure Functions**, **Azure OpenAI** e **Evolution API**. Diferente de bots síncronos simples, esta solução resolve o problema de "mensagens fragmentadas" (quando o usuário manda várias mensagens curtas seguidas), evitando múltiplas respostas desconexas da IA.

Esta arquitetura implementa um **Padrão de Debounce** usando Redis e Azure Storage Queues. Ela armazena temporariamente as mensagens, aguarda o usuário parar de digitar, agrega o contexto e envia uma única resposta coerente via GPT-3.5. Também conta com bloqueio distribuído para controle de concorrência.

## Arquitetura

O sistema opera em um modelo **Produtor/Consumidor** para garantir alta escalabilidade.

* **Ingestão (Function Producer)** – Recebe o Webhook da **Evolution API**. Armazena o texto da mensagem em uma Lista no Redis e agenda uma mensagem na **Azure Storage Queue** com um tempo de visibilidade (ex: 5 segundos).
* **Processamento (Function Consumer)** – Acionado quando a mensagem da fila se torna visível. Verifica no Redis se houve nova atividade ("Debounce Check"); se sim, aborta a execução.
* **Lock Distribuído** – Garante que apenas uma instância processe o chat de um usuário por vez.
* **Geração de IA** – Agrega todo o texto armazenado, recupera o histórico de chat e chama o **Azure OpenAI**.
* **Evolution API** – Gateway Dockerizado que conecta ao WhatsApp Web.

## Tecnologias Chave

* **C# (.NET 8 Isolated Worker)** – Lógica principal rodando em Azure Functions.
* **Azure Storage Queues** – Sinalização assíncrona e timeouts de visibilidade.
* **StackExchange.Redis** – Cliente de alta performance para buffer e locks.
* **Azure OpenAI Service** – Utiliza o `gpt-35-turbo`.
* **Evolution API v2** – API de integração com WhatsApp.
* **Docker Compose** – Orquestra o ambiente local (Postgres, Redis, Evolution, Azurite).

## Configuração

### Pré-requisitos

* **Docker Desktop** – Necessário para rodar a infraestrutura local.
* **Recurso Azure OpenAI** – Endpoint e Chave de API válidos.
* **.NET 8 SDK** – Para rodar o projeto.
* **Git** – Controle de versão.

### Configuração Local

1.  **Clone o Repositório**:

    ```bash
    git clone [https://github.com/SeuUsuario/BKBot.git](https://github.com/SeuUsuario/BKBot.git)
    cd BKBot
    ```

2.  **Variáveis de Ambiente**: Crie um arquivo `local.settings.json` na raiz do projeto Functions:

    ```json
    {
      "IsEncrypted": false,
      "Values": {
        "AzureWebJobsStorage": "UseDevelopmentStorage=true",
        "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
        "RedisConnection": "localhost:6379",
        "GPT_Endpoint": "[https://SEU-RECURSO.openai.azure.com/](https://SEU-RECURSO.openai.azure.com/)",
        "GPT_Key": "SUA-CHAVE-OPENAI",
        "Docker-Evolution": "42968F67V9DF9V76HDSF98V7HDE10F7D57E11"
      }
    }
    ```

### Rodando com Docker

Este projeto depende de serviços locais iniciados via Docker Compose.

* **Iniciar Infraestrutura**: Navegue até a pasta do `docker-compose.yml` e execute:

    ```bash
    docker-compose up -d
    ```

    Isso iniciará a Evolution API, Postgres, Redis e Azurite.

* **Configurar WhatsApp**:
    1.  Acesse `http://localhost:8080/manager`.
    2.  Login com a API Key: `42968F67V9DF9V76HDSF98V7HDE10F7D57E11`.
    3.  Crie uma instância **BKBot** e leia o QR Code.
    4.  **Importante:** Defina a URL de Webhook para `http://host.docker.internal:7071/api/message`.

* **Rodar a Aplicação .NET**:
    No terminal, na pasta do projeto:

    ```bash
    dotnet restore
    dotnet start
    ```

## Principais Trechos de Código

* **Lógica de Debounce (Produtor)**: Fazendo buffer e agendando com delay.

    ```csharp
    // Faz buffer da mensagem no Redis
    await _bufferService.AddToBufferAsync(messageData.Phone, messageData.Text);

    // Agenda gatilho com atraso (DebounceSeconds = 5)
    await _queueClient.SendMessageAsync(
        BinaryData.FromString(jsonMessage),
        visibilityTimeout: TimeSpan.FromSeconds(DebounceSeconds), 
        timeToLive: TimeSpan.FromMinutes(60)
    );
    ```

* **Consumindo o Buffer**: Verificando se é hora de processar.

    ```csharp
    TimeSpan timeSinceLastMsg = await _bufferService.GetTimeSinceLastActivityAsync(phone);

    // Se usuário digitou recentemente, ignora este gatilho
    if (timeSinceLastMsg < _debounceThreshold) return; 

    // Recupera tudo como um bloco único
    string consolidatedText = await _bufferService.GetAndClearBufferAsync(phone);
    ```
