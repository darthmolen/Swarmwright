using Swarmwright.Models.Enums;

namespace Swarmwright.Hosting.StateMachine;

/// <summary>
/// Enumerates and validates the legal transitions of <see cref="SwarmInstanceState"/>
/// and <see cref="TaskState"/>. Guards are pure: no I/O, no DB, no side effects.
/// </summary>
public static class SwarmStateGuards
{
    private static readonly Dictionary<SwarmInstanceState, HashSet<SwarmInstanceState>> SwarmTransitions = new()
    {
        [SwarmInstanceState.Created] = new()
        {
            SwarmInstanceState.Planning,
            SwarmInstanceState.Cancelled,
            SwarmInstanceState.Failed,
            SwarmInstanceState.AwaitingFeedback,
        },
        [SwarmInstanceState.Planning] = new()
        {
            SwarmInstanceState.Spawning,
            SwarmInstanceState.Cancelled,
            SwarmInstanceState.Failed,
            SwarmInstanceState.AwaitingFeedback,
        },
        [SwarmInstanceState.Spawning] = new()
        {
            SwarmInstanceState.Executing,
            SwarmInstanceState.Cancelled,
            SwarmInstanceState.Failed,
            SwarmInstanceState.AwaitingFeedback,
        },
        [SwarmInstanceState.Executing] = new()
        {
            SwarmInstanceState.AwaitingIntervention,
            SwarmInstanceState.Synthesizing,
            SwarmInstanceState.Cancelled,
            SwarmInstanceState.Failed,
            SwarmInstanceState.AwaitingFeedback,
        },
        [SwarmInstanceState.AwaitingIntervention] = new()
        {
            SwarmInstanceState.Executing,
            SwarmInstanceState.Synthesizing,
            SwarmInstanceState.NeedsDiagnosis,
            SwarmInstanceState.Cancelled,
            SwarmInstanceState.AwaitingFeedback,
        },
        [SwarmInstanceState.NeedsDiagnosis] = new()
        {
            SwarmInstanceState.Executing,
            SwarmInstanceState.Synthesizing,
            SwarmInstanceState.Cancelled,
            SwarmInstanceState.Failed,
            SwarmInstanceState.AwaitingFeedback,
        },
        [SwarmInstanceState.AwaitingFeedback] = new()
        {
            SwarmInstanceState.Created,
            SwarmInstanceState.Planning,
            SwarmInstanceState.Spawning,
            SwarmInstanceState.Executing,
            SwarmInstanceState.AwaitingIntervention,
            SwarmInstanceState.NeedsDiagnosis,
            SwarmInstanceState.Synthesizing,
            SwarmInstanceState.Cancelled,
            SwarmInstanceState.Failed,
        },
        [SwarmInstanceState.Synthesizing] = new()
        {
            SwarmInstanceState.Complete,
            SwarmInstanceState.Failed,
            SwarmInstanceState.Cancelled,
            SwarmInstanceState.AwaitingFeedback,
        },
        [SwarmInstanceState.Complete] = new(),
        [SwarmInstanceState.Cancelled] = new(),

        // Failed is terminal in the normal-flow sense — `IsTerminal(Failed)`
        // stays true and every standard intervention endpoint 410s — but the
        // admin Recover action can reopen a Failed swarm into
        // AwaitingIntervention. This is intentional: transient errors
        // captured by SwarmOrchestrator.RunAsync's catch-all would otherwise
        // strand real work forever.
        [SwarmInstanceState.Failed] = new()
        {
            SwarmInstanceState.AwaitingIntervention,
        },
    };

    private static readonly Dictionary<TaskState, HashSet<TaskState>> TaskTransitions = new()
    {
        [TaskState.Blocked] = new()
        {
            TaskState.Pending,
            TaskState.Failed,
        },
        [TaskState.Pending] = new()
        {
            TaskState.InProgress,
            TaskState.Blocked,
            TaskState.Failed,
        },
        [TaskState.InProgress] = new()
        {
            TaskState.Completed,
            TaskState.Failed,
            TaskState.AwaitingFeedback,

            // Legal only via orphan_resume — see SwarmInterventionHandler.ContinueAsync.
            // InProgress tasks are normally terminal for the round loop; this transition
            // exists solely for the crash-recovery / orphan-reset path. The guard does
            // not enforce reason strings, so this comment IS the enforcement.
            TaskState.Pending,
        },
        [TaskState.AwaitingFeedback] = new()
        {
            TaskState.InProgress,
            TaskState.Failed,
        },
        [TaskState.Failed] = new()
        {
            TaskState.Pending,
        },
        [TaskState.Completed] = new(),
    };

    /// <summary>
    /// Returns a set containing every terminal swarm state (no outbound transitions).
    /// </summary>
    /// <returns>A set of terminal <see cref="SwarmInstanceState"/> values.</returns>
    public static IReadOnlySet<SwarmInstanceState> TerminalSwarmStates()
    {
        return new HashSet<SwarmInstanceState>
        {
            SwarmInstanceState.Complete,
            SwarmInstanceState.Cancelled,
            SwarmInstanceState.Failed,
        };
    }

    /// <summary>
    /// Returns <c>true</c> when moving from <paramref name="from"/> to <paramref name="to"/>
    /// is declared legal for a swarm instance.
    /// </summary>
    /// <param name="from">The current state.</param>
    /// <param name="to">The target state.</param>
    /// <returns><c>true</c> if the transition is legal; otherwise <c>false</c>.</returns>
    public static bool CanTransitionSwarm(SwarmInstanceState from, SwarmInstanceState to)
    {
        if (from == to)
        {
            return true;
        }

        return SwarmTransitions.TryGetValue(from, out var targets) && targets.Contains(to);
    }

    /// <summary>
    /// Returns <c>true</c> when moving from <paramref name="from"/> to <paramref name="to"/>
    /// is declared legal for a task.
    /// </summary>
    /// <param name="from">The current state.</param>
    /// <param name="to">The target state.</param>
    /// <returns><c>true</c> if the transition is legal; otherwise <c>false</c>.</returns>
    public static bool CanTransitionTask(TaskState from, TaskState to)
    {
        if (from == to)
        {
            return true;
        }

        return TaskTransitions.TryGetValue(from, out var targets) && targets.Contains(to);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="state"/> has no outbound transitions.
    /// </summary>
    /// <param name="state">The state to test.</param>
    /// <returns><c>true</c> if terminal; otherwise <c>false</c>.</returns>
    public static bool IsTerminal(SwarmInstanceState state)
    {
        return TerminalSwarmStates().Contains(state);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="state"/> has no outbound transitions.
    /// </summary>
    /// <param name="state">The state to test.</param>
    /// <returns><c>true</c> if terminal; otherwise <c>false</c>.</returns>
    public static bool IsTerminal(TaskState state)
    {
        return state == TaskState.Completed;
    }
}
