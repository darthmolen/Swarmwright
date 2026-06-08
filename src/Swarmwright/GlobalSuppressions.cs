using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "Broker emission is fire-and-forget per IStateTransitionService XML contract; a broker exception must not roll back the already-persisted transition.",
    Scope = "member",
    Target = "~M:Swarmwright.Hosting.StateMachine.StateTransitionService.TransitionTaskAsync(System.Guid,System.String,Swarmwright.Models.Enums.TaskState,System.String,System.String,System.Int32,System.String,System.String,System.Threading.CancellationToken)~System.Threading.Tasks.Task{Swarmwright.Hosting.StateMachine.TaskStateTransitionResult}")]
