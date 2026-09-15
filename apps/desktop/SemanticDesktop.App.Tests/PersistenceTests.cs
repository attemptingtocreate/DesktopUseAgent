using SemanticDesktop.App.Models;
using SemanticDesktop.App.Persistence;

namespace SemanticDesktop.App.Tests;

public class PersistenceTests
{
    [Fact]
    public void Conversation_Save_Load_Delete_Does_Not_Store_Secrets()
    {
        var root = TestHarness.TempRoot();
        try
        {
            var store = new ConversationStore(root);
            var creds = new CredentialStore(root);
            creds.Set(CredentialStore.ProviderApiKey("openai"), "sk-secret-value-do-not-leak");

            var saved = store.Save(new Conversation
            {
                Title = "Secret-free",
                Messages =
                {
                    new ChatMessage { Id = "m1", Role = ChatRoles.User, Content = "hello" }
                }
            });

            var loaded = store.Get(saved.Id);
            Assert.NotNull(loaded);
            Assert.Equal("Secret-free", loaded!.Title);
            Assert.Single(loaded.Messages);

            var json = File.ReadAllText(Path.Combine(root, "conversations", saved.Id + ".json"));
            Assert.DoesNotContain("sk-secret-value-do-not-leak", json);
            Assert.DoesNotContain("apiKey", json);
            Assert.DoesNotContain("\"secrets\"", json);

            Assert.True(store.Delete(saved.Id));
            Assert.Null(store.Get(saved.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CredentialStore_Dpapi_Roundtrip()
    {
        var root = TestHarness.TempRoot();
        try
        {
            var store = new CredentialStore(root);
            var key = CredentialStore.ProviderApiKey("openai");
            store.Set(key, "sk-roundtrip-123");
            Assert.Equal("sk-roundtrip-123", store.Get(key));
            Assert.True(store.Exists(key));

            var files = Directory.GetFiles(Path.Combine(root, "secrets"), "*.dpapi");
            Assert.Single(files);
            var raw = File.ReadAllText(files[0]);
            Assert.DoesNotContain("sk-roundtrip-123", raw);

            var reopened = new CredentialStore(root);
            Assert.Equal("sk-roundtrip-123", reopened.Get(key));
            Assert.True(reopened.Delete(key));
            Assert.Null(reopened.Get(key));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
