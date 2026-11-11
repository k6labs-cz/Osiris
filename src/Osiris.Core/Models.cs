using System.ClientModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Osiris.Core;

/// <summary>
/// The workspace's model as its configuration says now: the clients are made on first use and again whenever
/// <c>osiris.json</c> changes, so the agent never needs a restart for a new model.
/// </summary>
public sealed partial class Models(Workspace workspace, AgentOptions host, Secrets secrets)
{
    readonly Lock gate = new();
    DateTime stamp = DateTime.MinValue;
    IChatClient? chat;
    IEmbeddingGenerator<string, Embedding<float>>? embeddings;

    public IChatClient Chat { get => field ??= new ChatClient(this); }
    public IEmbeddingGenerator<string, Embedding<float>> Embeddings { get => field ??= new EmbeddingClient(this); }

    /// <summary>The workspace's model over the host's; no key yet.</summary>
    public ModelConfig Configured() => OsirisConfig.Load(workspace.ConfigFile).Model.Over(host.Model);

    (IChatClient Chat, IEmbeddingGenerator<string, Embedding<float>> Embeddings) Current()
    {
        lock (gate)
        {
            var now = File.Exists(workspace.ConfigFile) ? File.GetLastWriteTimeUtc(workspace.ConfigFile) : default;
            if (chat is not null && embeddings is not null && now == stamp) return (chat, embeddings);

            var own = OsirisConfig.Load(workspace.ConfigFile).Model;
            var model = own.Over(host.Model);
            var options = new OpenAIClientOptions { Endpoint = new Uri(model.Endpoint!) };
            var key = new ApiKeyCredential(ApiKey(own, model.Endpoint!));
            chat = new OpenAI.Chat.ChatClient(model.Name, key, options).AsIChatClient();
            embeddings = new OpenAI.Embeddings.EmbeddingClient(model.Embedding, key, options).AsIEmbeddingGenerator();
            stamp = now;
            return (chat, embeddings);
        }
    }

    string ApiKey(ModelConfig own, string endpoint)
    {
        // The host's key goes only to the host's endpoint: a workspace naming another one brings its own key.
        var sameEndpoint = own.Endpoint is null || string.Equals(own.Endpoint.TrimEnd('/'), host.Model.Endpoint?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        if (own.ApiKey is null)
        {
            if (sameEndpoint && !string.IsNullOrEmpty(host.Model.ApiKey)) return host.Model.ApiKey;
            // A local model (Ollama, LM Studio) takes any key.
            if (host.Local && Uri.TryCreate(endpoint, UriKind.Absolute, out var url) && url.IsLoopback) return "local";
            throw new ConfigurationException(sameEndpoint
                ? "No model API key. Set OSIRIS_API_KEY, or \"model\": { \"apiKey\": \"{{env:NAME}}\" } in osiris.json."
                : "The workspace sets its own model endpoint, so it needs its own key: \"model\": { \"apiKey\": \"{{secret:NAME}}\" } in .osiris/osiris.json.");
        }

        var key = Env().Replace(own.ApiKey, m => host.Local
            ? Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? throw new ConfigurationException($"The environment variable {m.Groups[1].Value} (the model's API key) isn't set.")
            : throw new ConfigurationException("{{env:…}} works only on your own machine; on a server keep the model's key as a workspace secret: {{secret:NAME}}."));
        try
        {
            key = secrets.Fill(new Dictionary<string, string> { ["key"] = key }, Secrets.HostOf(endpoint)).Filled["key"];
        }
        catch (SecretException e)
        {
            throw new ConfigurationException($"The model's API key: {e.Message}");
        }
        if (key.Contains("{{")) throw new ConfigurationException("The model's apiKey must be {{secret:NAME}} or {{env:NAME}}.");
        // A key written into osiris.json would end in git.
        if (key == own.ApiKey) throw new ConfigurationException("Don't write the model's API key into osiris.json: use {{secret:NAME}} or {{env:NAME}}.");
        return key;
    }

    [GeneratedRegex(@"\{\{\s*env:([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Env();

    sealed class ChatClient(Models models) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            models.Current().Chat.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            models.Current().Chat.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    sealed class EmbeddingClient(Models models) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            models.Current().Embeddings.GenerateAsync(values, options, cancellationToken);

        /// <summary>The configured model's name without a client, so asking for it never needs the key.</summary>
        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceType != typeof(EmbeddingGeneratorMetadata)) return null;
            try { return new EmbeddingGeneratorMetadata("openai", defaultModelId: models.Configured().Embedding); }
            catch (ConfigurationException) { return null; }
        }

        public void Dispose() { }
    }
}
