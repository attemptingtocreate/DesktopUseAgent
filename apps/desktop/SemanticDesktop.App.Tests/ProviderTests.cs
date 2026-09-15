using SemanticDesktop.App.Models;
using SemanticDesktop.App.Persistence;
using SemanticDesktop.App.Providers;

namespace SemanticDesktop.App.Tests;

public class ProviderAbstractionTests
{
    [Fact]
    public async Task Fake_Provider_Connect_ListModels_Complete_Text()
    {
        var provider = TestHarness.TextProvider("fake-1", "hello world");
        provider.Models.Add(new ModelRecord { Id = "m1", DisplayName = "Model 1", ProviderId = "fake-1" });

        var status = await provider.ConnectAsync();
        Assert.Equal(ProviderConnectionStatusKind.Connected, status.Kind);

        var models = await provider.ListModelsAsync();
        Assert.Contains(models, m => m.Id == "m1");

        var chunks = new List<string>();
        await foreach (var ev in provider.CompleteAsync(new AgentRequest
        {
            Messages = new[] { new AgentMessage { Role = "user", Content = "hi" } }
        }, CancellationToken.None))
        {
            if (ev.Kind == AgentProviderEventKind.TextDelta)
            {
                chunks.Add(ev.Text!);
            }
        }

        Assert.Equal(new[] { "hello world" }, chunks);
        Assert.Single(provider.ReceivedRequests);
    }

    [Fact]
    public async Task Provider_Switching_Preserves_Prior_Turn_ProviderId()
    {
        var a = new FakeAgentProvider("prov-a", "fake", "A");
        a.Script.Add(new FakeProviderTurn
        {
            Events = { AgentProviderEvent.TextDelta("from-a"), AgentProviderEvent.Completed() }
        });
        var b = new FakeAgentProvider("prov-b", "fake", "B");
        b.Script.Add(new FakeProviderTurn
        {
            Events = { AgentProviderEvent.TextDelta("from-b"), AgentProviderEvent.Completed() }
        });

        var host = TestHarness.ChatHost(new IAgentProvider[] { a, b });
        var convo = host.ConversationsApi.Create(providerId: "prov-a");
        await host.ConversationsApi.SendAsync(convo.Id, "first");
        host.ConversationsApi.SetSelectedProvider(convo.Id, "prov-b");
        await host.ConversationsApi.SendAsync(convo.Id, "second");

        var loaded = host.ConversationsApi.Get(convo.Id)!;
        Assert.Equal(2, loaded.Turns.Count);
        Assert.Equal("prov-a", loaded.Turns[0].ProviderId);
        Assert.Equal("prov-b", loaded.Turns[1].ProviderId);
        Assert.Equal("prov-b", loaded.SelectedProviderId);
        Assert.Contains(loaded.Messages, m => m.Content == "from-a");
        Assert.Contains(loaded.Messages, m => m.Content == "from-b");
    }

    [Fact]
    public async Task UnsupportedProvider_Cannot_Complete_Usefully()
    {
        var provider = new UnsupportedProvider(new ProviderConfig
        {
            Id = "web",
            Type = "chatgpt-web",
            DisplayName = "ChatGPT Web"
        });

        var connect = await provider.ConnectAsync();
        Assert.Equal(ProviderConnectionStatusKind.Unsupported, connect.Kind);
        Assert.Contains("not supported", connect.Message, StringComparison.OrdinalIgnoreCase);

        var events = new List<AgentProviderEvent>();
        await foreach (var ev in provider.CompleteAsync(new AgentRequest(), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Contains(events, e => e.Kind == AgentProviderEventKind.Error);
        Assert.Empty(await provider.ListModelsAsync());
    }
}
