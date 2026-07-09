using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Swarmwright.Database.Models;
using Swarmwright.Models;
using Swarmwright.Templates;
using Swarmwright.Tools;

namespace Swarmwright.Extensions;

/// <summary>
/// Default <see cref="ILeaderRepairAdvisor"/>. Invokes the shared leader
/// <see cref="IChatClient"/> with the swarm's template-defined leader
/// prompt plus a summary of the failed tasks, exposing the
/// <c>repair_plan_after_failure</c> tool. Captures the leader's tool
/// call and returns the resulting <see cref="RepairPlan"/>.
/// </summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "LLM failures fold into a null return; the handler surfaces 409.")]
public sealed partial class DefaultLeaderRepairAdvisor : ILeaderRepairAdvisor
{
    private const string FallbackLeaderPrompt =
        "You are the swarm leader. The execution has encountered task failures. "
      + "Review the failed tasks below and call `repair_plan_after_failure` exactly once "
      + "with a minimal, surgical set of changes (reset tasks that look recoverable, add "
      + "new tasks to replace the ones you abandon, and abandon any task whose premise "
      + "is broken). Include a short rationale in `note`.";

    private readonly IChatClient leaderChatClient;
    private readonly ITemplateLoader? templateLoader;
    private readonly ILogger<DefaultLeaderRepairAdvisor> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultLeaderRepairAdvisor"/> class.
    /// </summary>
    /// <param name="leaderChatClient">The shared leader chat client.</param>
    /// <param name="templateLoader">Optional template loader used to pull the swarm-specific leader prompt.</param>
    /// <param name="loggerFactory">A logger factory for diagnostic messages.</param>
    public DefaultLeaderRepairAdvisor(
        IChatClient leaderChatClient,
        ITemplateLoader? templateLoader,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(leaderChatClient);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.leaderChatClient = leaderChatClient;
        this.templateLoader = templateLoader;
        this.logger = loggerFactory.CreateLogger<DefaultLeaderRepairAdvisor>();
    }

    /// <inheritdoc/>
    public async Task<RepairPlan?> RequestRepairAsync(
        Guid swarmId,
        IReadOnlyList<TaskEntity> failedTasks,
        string? templateKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failedTasks);

        var (repairTool, planSource) = LeaderToolFactory.CreateRepairPlanTool();

        var systemPrompt = this.ResolveLeaderPrompt(templateKey);
        var userPrompt = BuildRepairPrompt(swarmId, failedTasks);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt),
        };

        var options = new ChatOptions
        {
            Tools = [repairTool],
        };

        try
        {
            await this.leaderChatClient
                .GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogLeaderCallFailed(swarmId, ex);
            return null;
        }

        if (!planSource.Task.IsCompletedSuccessfully)
        {
            this.LogLeaderDidNotCallTool(swarmId);
            return null;
        }

        return await planSource.Task.ConfigureAwait(false);
    }

    private string ResolveLeaderPrompt(string? templateKey)
    {
        if (this.templateLoader is null || string.IsNullOrWhiteSpace(templateKey))
        {
            return FallbackLeaderPrompt;
        }

        try
        {
            var template = this.templateLoader.Load(templateKey);
            var leaderPrompt = template?.LeaderPrompt;
            if (string.IsNullOrWhiteSpace(leaderPrompt))
            {
                return FallbackLeaderPrompt;
            }

            return leaderPrompt + "\n\n" + FallbackLeaderPrompt;
        }
        catch
        {
            return FallbackLeaderPrompt;
        }
    }

    private static string BuildRepairPrompt(Guid swarmId, IReadOnlyList<TaskEntity> failedTasks)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Swarm id: {swarmId}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"Failed tasks ({failedTasks.Count}):").AppendLine();
        foreach (var t in failedTasks)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- id={t.Id} worker={t.WorkerName} retry_count={t.RetryCount}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"  subject: {t.Subject}").AppendLine();
            if (!string.IsNullOrWhiteSpace(t.Description))
            {
                sb.Append(CultureInfo.InvariantCulture, $"  description: {t.Description}").AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(t.Result))
            {
                sb.Append(CultureInfo.InvariantCulture, $"  last result: {t.Result}").AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("Call repair_plan_after_failure now with the minimal change set.");
        return sb.ToString();
    }

    [LoggerMessage(LogLevel.Warning, "Leader repair LLM call failed for swarm {SwarmId}.")]
    private partial void LogLeaderCallFailed(Guid swarmId, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Leader did not invoke repair_plan_after_failure for swarm {SwarmId}.")]
    private partial void LogLeaderDidNotCallTool(Guid swarmId);
}
