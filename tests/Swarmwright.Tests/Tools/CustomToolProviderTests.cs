using Swarmwright.Tools;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Swarmwright.Tests.Tools;

/// <summary>
/// Tests for <see cref="CustomToolProvider"/> reflection-based tool discovery.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CustomToolProviderTests
{
    /// <summary>
    /// All methods decorated with [SwarmTool] are returned as AITool instances.
    /// </summary>
    [TestMethod]
    public void GetTools_DiscoversAttributedMethods()
    {
        var provider = new TwoToolProvider();

        var tools = provider.GetTools();

        tools.Should().HaveCount(2);
        tools.Select(t => t.Name).Should().BeEquivalentTo(["tool_a", "tool_b"]);
    }

    /// <summary>
    /// Methods without the [SwarmTool] attribute are skipped.
    /// </summary>
    [TestMethod]
    public void GetTools_IgnoresMethodsWithoutAttribute()
    {
        var provider = new MixedMethodsProvider();

        var tools = provider.GetTools();

        tools.Should().HaveCount(1);
        tools[0].Name.Should().Be("decorated");
    }

    /// <summary>
    /// Tool names and descriptions come from the [SwarmTool] attribute values.
    /// </summary>
    [TestMethod]
    public void GetTools_UsesAttributeNameAndDescription()
    {
        var provider = new TwoToolProvider();

        var tools = provider.GetTools();

        var toolA = tools.First(t => t.Name == "tool_a");
        toolA.Description.Should().Be("First tool.");
    }

    /// <summary>
    /// Methods returning Task&lt;T&gt; are invocable via AIFunction.InvokeAsync and
    /// the awaited result reaches the caller.
    /// </summary>
    [TestMethod]
    public async Task GetTools_SupportsAsyncMethods()
    {
        var provider = new AsyncProvider();

        var tools = provider.GetTools();

        var asyncTool = tools.OfType<AIFunction>().First(t => t.Name == "fetch");
        var result = await asyncTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["input"] = "hello" }));

        result.Should().NotBeNull();
        result!.ToString().Should().Contain("fetched:hello");
    }

    /// <summary>
    /// Constructor-injected dependencies are available when tool methods are invoked.
    /// </summary>
    [TestMethod]
    public async Task GetTools_InjectsDependenciesFromConstructor()
    {
        var backingStore = new List<string>();
        var provider = new DependentProvider(backingStore);

        var tools = provider.GetTools();
        var recordTool = tools.OfType<AIFunction>().First(t => t.Name == "record");
        await recordTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["entry"] = "x" }));

        backingStore.Should().ContainSingle().Which.Should().Be("x");
    }

    /// <summary>
    /// Repeated calls to GetTools() return the same cached list.
    /// </summary>
    [TestMethod]
    public void GetTools_CachesResult_AcrossCalls()
    {
        var provider = new TwoToolProvider();

        var first = provider.GetTools();
        var second = provider.GetTools();

        second.Should().BeSameAs(first);
    }

#pragma warning disable CA1822 // instance methods by design — wrapped as delegates bound to `this`
    private sealed class TwoToolProvider : CustomToolProvider
    {
        [SwarmTool("tool_a", "First tool.")]
        public string ToolA() => "a";

        [SwarmTool("tool_b", "Second tool.")]
        public string ToolB() => "b";
    }

    private sealed class MixedMethodsProvider : CustomToolProvider
    {
        [SwarmTool("decorated", "Attributed method.")]
        public string Decorated() => "d";

        public string Undecorated() => "u";
    }

    private sealed class AsyncProvider : CustomToolProvider
    {
        [SwarmTool("fetch", "Fetches a value asynchronously.")]
        public async Task<string> FetchAsync([System.ComponentModel.Description("The input.")] string input)
        {
            await Task.Yield();
            return $"fetched:{input}";
        }
    }
#pragma warning restore CA1822

    private sealed class DependentProvider : CustomToolProvider
    {
        private readonly List<string> store;

        public DependentProvider(List<string> store)
        {
            this.store = store;
        }

        [SwarmTool("record", "Records an entry.")]
        public string Record([System.ComponentModel.Description("The entry to record.")] string entry)
        {
            this.store.Add(entry);
            return "ok";
        }
    }
}
