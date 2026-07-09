using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Swarmwright.Configuration;
using Swarmwright.Core;
using Swarmwright.Database.Repositories;
using Swarmwright.Events.AgUI;
using Swarmwright.Hosting.StateMachine;
using Swarmwright.Mcp;
using Swarmwright.Orchestration;
using Swarmwright.Services;
using Swarmwright.Templates;

namespace Swarmwright.Hosting;

/// <summary>
/// Default <see cref="ISwarmOrchestratorFactory"/>. Resolves per-swarm
/// services from the supplied scope and constructs a <see cref="SwarmOrchestrator"/>
/// ready to run. Shared by <see cref="SwarmDispatcherService"/> (for fresh
/// runs) and the swarm rehydrator (for evicted-swarm wake-ups).
/// </summary>
public sealed class SwarmOrchestratorFactory : ISwarmOrchestratorFactory
{
    private readonly SwarmOptions options;
    private readonly ILoggerFactory loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmOrchestratorFactory"/> class.
    /// </summary>
    /// <param name="options">The swarm configuration options.</param>
    /// <param name="loggerFactory">A logger factory for per-orchestrator loggers.</param>
    public SwarmOrchestratorFactory(
        IOptions<SwarmOptions> options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.options = options.Value;
        this.loggerFactory = loggerFactory;
    }

    /// <inheritdoc/>
    public ISwarmOrchestrator Build(IServiceScope scope, SwarmExecution execution)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(execution);

        var sp = scope.ServiceProvider;
        var chatClient = sp.GetRequiredService<IChatClient>();
        var inboxSystem = sp.GetRequiredService<IInboxSystem>();
        var teamRegistry = sp.GetRequiredService<ITeamRegistry>();
        var repository = sp.GetRequiredService<ISwarmRepository>();

        var stateTransitionService = sp.GetRequiredService<IStateTransitionService>();

        var swarmService = new SwarmService(inboxSystem, teamRegistry, repository);

        LoadedTemplate? template = null;
        string? templatesDirectory = null;
        if (execution.TemplateKey != null)
        {
            var loader = sp.GetRequiredService<ITemplateLoader>();
            template = loader.Load(execution.TemplateKey);
            templatesDirectory = loader.TemplatesDirectory;
        }

        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("swarm-default-tools");

        var mcpClientFactory = sp.GetService<IMcpClientFactory>();
        Func<string, CancellationToken, Task<IReadOnlyList<AITool>>>? mcpToolLoader = mcpClientFactory is null
            ? null
            : async (endpointName, ct) =>
            {
                var client = await mcpClientFactory.GetOrCreateClientAsync(endpointName, ct).ConfigureAwait(false);
                var mcpTools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
                return mcpTools.Cast<AITool>().ToList();
            };

        var customToolProviders = sp.GetServices<Tools.ICustomToolProvider>();

        return new SwarmOrchestrator(
            chatClient,
            workerName => new AgUIEventInterceptor(chatClient, execution.AgUiAdapter, workerName),
            execution.EventBus,
            execution.AgUiAdapter,
            swarmService,
            stateTransitionService,
            this.options,
            template,
            execution.WorkDirectory,
            httpClient,
            this.loggerFactory,
            mcpToolLoader,
            templatesDirectory,
            customToolProviders);
    }
}
