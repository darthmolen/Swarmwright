using Swarmwright.Models.Enums;

namespace Swarmwright.Orchestration;

/// <summary>
/// Represents the outcome of a single worker task execution, including whether the worker
/// explicitly declared completion via the <c>task_update</c> tool.
/// </summary>
/// <param name="FinalText">The final assistant text emitted by the worker, if any.</param>
/// <param name="WorkerDeclaredStatus">
/// The status declared by the worker via a successful <c>task_update</c> tool invocation,
/// or <see langword="null"/> if the worker never successfully signalled a terminal status.
/// </param>
/// <param name="WorkerDeclaredResult">
/// The optional result text the worker passed to the <c>task_update</c> tool, or
/// <see langword="null"/> if no result was supplied.
/// </param>
public sealed record TaskExecutionResult(
    string FinalText,
    TaskState? WorkerDeclaredStatus,
    string? WorkerDeclaredResult);
