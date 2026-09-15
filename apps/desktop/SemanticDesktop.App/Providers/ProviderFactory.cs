using SemanticDesktop.App.Persistence;

namespace SemanticDesktop.App.Providers;

public sealed class ProviderFactory
{
    public IAgentProvider Create(ProviderConfig config, CredentialStore credentials, HttpMessageHandler? handler = null)
    {
        var type = (config.Type ?? "").Trim().ToLowerInvariant();
        return type switch
        {
            "openai" or "openai-compatible" or "openai_compatible" => new OpenAiCompatibleProvider(config, credentials, handler),
            "anthropic" or "claude" => new AnthropicProvider(config, credentials, handler),
            "ollama" => new OllamaProvider(config, handler),
            "fake" => new FakeAgentProvider(config.Id, "fake", config.DisplayName),
            _ => new UnsupportedProvider(config)
        };
    }
}

public sealed class ConfigProviderResolver : IProviderResolver
{
    private readonly ProviderConfigStore _store;
    private readonly CredentialStore _credentials;
    private readonly ProviderFactory _factory;
    private readonly HttpMessageHandler? _handler;

    public ConfigProviderResolver(
        ProviderConfigStore store,
        CredentialStore credentials,
        ProviderFactory? factory = null,
        HttpMessageHandler? handler = null)
    {
        _store = store;
        _credentials = credentials;
        _factory = factory ?? new ProviderFactory();
        _handler = handler;
    }

    public IAgentProvider Resolve(string providerId)
    {
        var config = _store.Get(providerId) ?? throw new InvalidOperationException($"Unknown provider '{providerId}'.");
        return _factory.Create(config, _credentials, _handler);
    }
}
