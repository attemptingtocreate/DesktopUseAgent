using SemanticDesktop.Core.Handles;

namespace SemanticDesktop.Core.Tests;

public class HandleRegistryTests
{
    [Fact]
    public void ElementRuntimeIndex_LookupAndCleanup()
    {
        var registry = new HandleRegistry();
        const string runtimeKey = "123|Button|save|Save|1-2-3";
        var metadata = new Dictionary<string, object?> { ["runtimeKey"] = runtimeKey };

        var id = registry.Allocate(HandleKind.Element, new object(), metadata, preferredId: "uia_test001");
        Assert.True(registry.TryGetElementByRuntimeKey(runtimeKey, out var found));
        Assert.Equal(id, found);
        Assert.Equal(1, registry.ElementRuntimeIndexCount);

        registry.Remove(id);
        Assert.False(registry.TryGetElementByRuntimeKey(runtimeKey, out _));
        Assert.Equal(0, registry.ElementRuntimeIndexCount);
    }

    [Fact]
    public void ElementRuntimeIndex_RepeatedLookup_StaysIndexedWithoutListScan()
    {
        var registry = new HandleRegistry();
        for (var i = 0; i < 100; i++)
        {
            var runtimeKey = $"key-{i}";
            registry.Allocate(
                HandleKind.Element,
                new object(),
                new Dictionary<string, object?> { ["runtimeKey"] = runtimeKey },
                preferredId: $"uia_{i:000}");
        }

        Assert.Equal(100, registry.ElementRuntimeIndexCount);
        for (var i = 0; i < 100; i++)
        {
            Assert.True(registry.TryGetElementByRuntimeKey($"key-{i}", out var id));
            Assert.StartsWith("uia_", id);
        }

        Assert.Equal(100, registry.ListIds(HandleKind.Element).Count);
    }

    [Fact]
    public void ElementRuntimeIndex_RemoveWhere_CleansStaleEntries()
    {
        var registry = new HandleRegistry();
        var ids = Enumerable.Range(0, 5)
            .Select(i => registry.Allocate(
                HandleKind.Element,
                new object(),
                new Dictionary<string, object?> { ["runtimeKey"] = $"rk-{i}" },
                preferredId: $"uia_{i:000}"))
            .ToList();

        registry.RemoveWhere(entry => entry.Metadata.TryGetValue("runtimeKey", out var key) && key as string == "rk-2");
        Assert.False(registry.TryGetElementByRuntimeKey("rk-2", out _));
        Assert.True(registry.TryGetElementByRuntimeKey("rk-0", out _));
        Assert.Equal(4, registry.ElementRuntimeIndexCount);
        _ = ids;
    }

    [Fact]
    public void ElementRuntimeIndex_ReplacesMetadataKeyWithoutStaleIndex()
    {
        var registry = new HandleRegistry();
        const string oldKey = "old-runtime-key";
        const string newKey = "new-runtime-key";
        var id = registry.Allocate(
            HandleKind.Element,
            new object(),
            new Dictionary<string, object?> { ["runtimeKey"] = oldKey },
            preferredId: "uia_replace");

        Assert.True(registry.TryGetElementByRuntimeKey(oldKey, out _));
        registry.Allocate(
            HandleKind.Element,
            new object(),
            new Dictionary<string, object?> { ["runtimeKey"] = newKey },
            preferredId: id);

        Assert.False(registry.TryGetElementByRuntimeKey(oldKey, out _));
        Assert.True(registry.TryGetElementByRuntimeKey(newKey, out var found));
        Assert.Equal(id, found);
        Assert.Equal(1, registry.ElementRuntimeIndexCount);
    }

    [Fact]
    public void ElementRuntimeIndex_RemovesStaleIndexWhenHandleMissing()
    {
        var registry = new HandleRegistry();
        var field = typeof(HandleRegistry).GetField("_elementRuntimeIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var index = (Dictionary<string, string>)field.GetValue(registry)!;
        index["orphan-key"] = "missing-id";

        Assert.False(registry.TryGetElementByRuntimeKey("orphan-key", out _));
        Assert.Equal(0, registry.ElementRuntimeIndexCount);
    }
}
