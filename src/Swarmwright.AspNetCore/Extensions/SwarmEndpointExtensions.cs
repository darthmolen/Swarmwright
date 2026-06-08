using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Swarmwright.Database.Repositories;
using Swarmwright.Events;
using Swarmwright.Exceptions;
using Swarmwright.Hosting;
using Swarmwright.Models;
using Swarmwright.Recommendation;
using Swarmwright.Refinement;
using Swarmwright.Templates;
using Swarmwright.Tools;

namespace Swarmwright.Extensions;

/// <summary>
/// Extension methods for mapping Swarm REST endpoints.
/// </summary>
public static partial class SwarmEndpointExtensions
{
    /// <summary>
    /// Maximum accepted length of the user-supplied goal string on
    /// <c>POST /api/swarm</c>. The goal gets stuffed into every worker's system
    /// prompt and multiplied across every LLM call, so an unbounded value is a
    /// token-cost attack vector.
    /// </summary>
    private const int MaxGoalLength = 16_384;

    /// <summary>
    /// Regex pattern matching valid template keys: alphanumeric plus underscore
    /// and dash, no path separators or traversal sequences. Matches
    /// <see cref="TemplateLoader"/>'s internal validator — the
    /// endpoint performs the same check up front for defense in depth and to
    /// return a clean 400 instead of an ArgumentException bubble.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex TemplateKeyRegex();

    /// <summary>
    /// Translates an <see cref="InterventionResult"/> into an ASP.NET Core
    /// <see cref="IResult"/>. Centralizes the transport mapping so the
    /// logic-core handlers can stay web-framework-agnostic.
    /// </summary>
    /// <param name="result">The handler's logical return value.</param>
    /// <returns>The corresponding <see cref="IResult"/>.</returns>
    private static IResult ToHttpResult(InterventionResult result)
    {
        return result.StatusCode switch
        {
            200 => Results.Json(result.Body, SwarmJsonOptions.Default),
            204 => Results.NoContent(),
            _ => Results.Json(result.Body, SwarmJsonOptions.Default, statusCode: result.StatusCode),
        };
    }

    /// <summary>
    /// Maps the Swarm REST API endpoints onto the given endpoint route builder.
    /// Uses Swarm.Read and Swarm.Write policies when <paramref name="useSwarmPolicies"/> is true.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="useSwarmPolicies">When true, applies Swarm.Read/Swarm.Write policies. When false, endpoints are anonymous.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapSwarmEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool useSwarmPolicies = false)
    {
        var group = endpoints.MapGroup("/api/swarm");

        var readPolicy = useSwarmPolicies ? SwarmAuthorizationExtensions.SwarmReadPolicy : null;
        var writePolicy = useSwarmPolicies ? SwarmAuthorizationExtensions.SwarmWritePolicy : null;

        // Write endpoints — require Swarm.Write scope
        var createEndpoint = group.MapPost(
            "/",
            async (CreateSwarmRequest request, ISwarmManager manager) =>
            {
                // Input validation hardens the HTTP boundary: unbounded goal
                // strings are a token-cost attack (stuffed into every system
                // prompt, multiplied across every agent LLM call) and malformed
                // templateKey would fail later in TemplateLoader with a less
                // actionable error.
                if (string.IsNullOrWhiteSpace(request.Goal))
                {
                    return Results.BadRequest(new { error = "goal is required" });
                }

                if (request.Goal.Length > MaxGoalLength)
                {
                    return Results.BadRequest(new
                    {
                        error = $"goal exceeds maximum length of {MaxGoalLength} characters",
                        actualLength = request.Goal.Length,
                    });
                }

                if (request.TemplateKey is not null && !TemplateKeyRegex().IsMatch(request.TemplateKey))
                {
                    return Results.BadRequest(new
                    {
                        error = "templateKey must match ^[A-Za-z0-9_-]+$ (no path separators or traversal sequences)",
                    });
                }

                var swarmId = await manager.CreateSwarmAsync(request.Goal, request.TemplateKey, request.Context).ConfigureAwait(false);
                return Results.Json(new { swarmId }, SwarmJsonOptions.Default, statusCode: 201);
            });

        var cancelEndpoint = group.MapPost(
            "/{id:guid}/cancel",
            async (Guid id, HttpContext httpContext, ISwarmManager manager, ISwarmInterventionHandler handler) =>
            {
                await manager.EnsureLiveAsync(id, httpContext.RequestAborted).ConfigureAwait(false);
                var actor = SwarmActorResolver.Resolve(httpContext);
                var result = await handler.CancelAsync(id, actor, httpContext.RequestAborted).ConfigureAwait(false);
                return ToHttpResult(result);
            });

        // The 3 recovery endpoints below no longer call manager.EnsureLiveAsync
        // before the handler. The handler owns orchestrator-lifecycle handoff
        // and calls EnsureLiveAsync AFTER its state writes, so the resurrected
        // orchestrator's LoadAsync reads the post-handler DB state. Moving
        // EnsureLiveAsync back here (pre-handler) re-introduces the race the
        // orphan-InProgress click hit on 2026-04-24.
        var continueEndpoint = group.MapPost(
            "/{id:guid}/continue",
            async (Guid id, HttpContext httpContext, ISwarmInterventionHandler handler) =>
            {
                var actor = SwarmActorResolver.Resolve(httpContext);
                var result = await handler.ContinueAsync(id, actor, httpContext.RequestAborted).ConfigureAwait(false);
                return ToHttpResult(result);
            });

        var smartContinueEndpoint = group.MapPost(
            "/{id:guid}/smart-continue",
            async (Guid id, HttpContext httpContext, ISwarmInterventionHandler handler) =>
            {
                var actor = SwarmActorResolver.Resolve(httpContext);
                var result = await handler.SmartContinueAsync(id, actor, httpContext.RequestAborted).ConfigureAwait(false);
                return ToHttpResult(result);
            });

        var skipEndpoint = group.MapPost(
            "/{id:guid}/skip",
            async (Guid id, HttpContext httpContext, ISwarmInterventionHandler handler) =>
            {
                var actor = SwarmActorResolver.Resolve(httpContext);
                var result = await handler.SkipAsync(id, actor, httpContext.RequestAborted).ConfigureAwait(false);
                return ToHttpResult(result);
            });

        var lockEndpoint = group.MapPost(
            "/{id:guid}/lock",
            async (Guid id, bool? steal, HttpContext httpContext, ISwarmInterventionHandler handler) =>
            {
                var actor = SwarmActorResolver.Resolve(httpContext);
                var result = await handler.LockAsync(id, actor, steal ?? false, httpContext.RequestAborted).ConfigureAwait(false);
                return ToHttpResult(result);
            });

        var unlockEndpoint = group.MapDelete(
            "/{id:guid}/lock",
            async (Guid id, HttpContext httpContext, ISwarmInterventionHandler handler) =>
            {
                var actor = SwarmActorResolver.Resolve(httpContext);
                var result = await handler.UnlockAsync(id, actor, httpContext.RequestAborted).ConfigureAwait(false);
                return ToHttpResult(result);
            });

        var markAsAwaitingInterventionEndpoint = group.MapPost(
            "/{id:guid}/mark-as-awaiting-intervention",
            async (Guid id, HttpContext httpContext, ISwarmInterventionHandler handler) =>
            {
                // No EnsureLiveAsync precall: Failed swarms are never in the
                // active-swarms dictionary, and the manager's EnsureLiveAsync
                // would short-circuit on the terminal guard anyway. Manual
                // Recover is a pure state flip; the operator picks a
                // recovery action from the AwaitingIntervention UI next.
                var actor = SwarmActorResolver.Resolve(httpContext);
                var result = await handler.MarkAsAwaitingInterventionAsync(id, actor, httpContext.RequestAborted).ConfigureAwait(false);
                return ToHttpResult(result);
            });

        var copilotEndpoint = group.MapPost(
            "/{id:guid}/copilot",
            async (Guid id, RefinementRequestDto request, RefinementChatHandler handler, HttpContext httpContext) =>
            {
                try
                {
                    await handler.HandleAsync(id, request, httpContext).ConfigureAwait(false);
                }
                catch (SwarmWorkDirNotFoundException ex)
                {
                    httpContext.Response.StatusCode = 404;
                    await httpContext.Response.WriteAsJsonAsync(new
                    {
                        error = "Swarm work directory not found",
                        swarmId = ex.SwarmId,
                        expectedPath = ex.ExpectedPath,
                    }).ConfigureAwait(false);
                }
            });

        // Read endpoints — require Swarm.Read scope
        var listEndpoint = group.MapGet(
            "/",
            async (
                ISwarmManager manager,
                ISwarmRepository repo,
                ILogger<SwarmListEndpointCategory> logger,
                int? limit,
                DateTime? since,
                CancellationToken ct) =>
            {
                var effectiveLimit = limit is > 0 and <= 500 ? limit.Value : 50;

                IReadOnlyList<Database.Models.SwarmListEntry> historical;
                try
                {
                    historical = await repo.ListAllSwarmsAsync(effectiveLimit, since, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Some deployments run without a configured database. Preserve the
                    // original endpoint behavior (the pre-batch-1 handler never touched
                    // the repository at all) by logging a warning and continuing with
                    // an empty historical set so the in-memory list still works.
                    SwarmListFallbackLogger.LogRepositoryUnavailable(logger, ex);
                    historical = Array.Empty<Database.Models.SwarmListEntry>();
                }

                var active = manager.ListActiveSwarms();
                var merged = SwarmListMerger.Merge(active, historical, effectiveLimit);

                // Results.Json with SwarmJsonOptions.Default — NOT Results.Ok — so the
                // response serialization (camelCase via JsonSerializerDefaults.Web +
                // PascalCase enum names via JsonStringEnumConverter) is controlled by
                // the library and not by whatever the downstream host has configured
                // for its framework-level JSON defaults. The swarm ships as a package
                // consumed inside someone else's host, and a host author may change
                // their JSON options without knowing our endpoints depend on them.
                return Results.Json(merged, SwarmJsonOptions.Default);
            });

        var getEndpoint = group.MapGet(
            "/{id:guid}",
            async (
                Guid id,
                ISwarmManager manager,
                ISwarmRepository repo,
                IRecommendedSwarmContinueProvider recommendationProvider,
                ILogger<SwarmListEndpointCategory> logger) =>
            {
                // Phase comes from the DB (single source of truth after the
                // state-machine migration). The dispatcher still holds the
                // SwarmExecution in memory for ~5 minutes after RunAsync
                // returns, but the phase itself is no longer mirrored on
                // SwarmExecution — we read the persisted state instead.
                var swarm = manager.GetSwarm(id);
                if (swarm is not null)
                {
                    Database.Models.SwarmEntity? liveEntity = null;
                    try
                    {
                        liveEntity = await repo.GetSwarmAsync(id).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        SwarmListFallbackLogger.LogMetadataRepositoryUnavailable(logger, id, ex);
                    }

                    var liveRecommendation = await recommendationProvider
                        .GetRecommendationAsync(id)
                        .ConfigureAwait(false);

                    return Results.Json(
                        new Database.Models.SwarmMetadataResponse(
                            SwarmId: swarm.SwarmId,
                            Goal: swarm.Goal,
                            TemplateKey: swarm.TemplateKey,
                            Phase: liveEntity?.State ?? "Created",
                            State: liveEntity?.State ?? "Created",
                            IsRunning: swarm.IsRunning,
                            LockedBy: liveEntity?.LockedBy,
                            LockedAt: liveEntity?.LockedAt is { } lockedAt ? DateTime.SpecifyKind(lockedAt, DateTimeKind.Utc) : null,
                            CreatedAt: DateTime.SpecifyKind(swarm.CreatedAt, DateTimeKind.Utc),
                            CompletedAt: null,
                            Recommendation: liveRecommendation),
                        SwarmJsonOptions.Default);
                }

                // Manager evicted the execution (or never owned it — another
                // tab / process created the swarm) so fall back to the DB.
                // Some deployments run without a configured database; preserve
                // the pre-batch-1 behavior (return 404) when the repository
                // throws so nothing downstream sees a partially-populated
                // response.
                Database.Models.SwarmEntity? entity;
                try
                {
                    entity = await repo.GetSwarmAsync(id).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SwarmListFallbackLogger.LogMetadataRepositoryUnavailable(logger, id, ex);
                    return Results.NotFound();
                }

                if (entity is null)
                {
                    return Results.NotFound();
                }

                var persistedRecommendation = await recommendationProvider
                    .GetRecommendationAsync(id)
                    .ConfigureAwait(false);

                // isRunning is always false on the DB path: if the swarm were
                // running, its SwarmExecution would still be in the in-memory
                // manager and the early-return above would have fired.
                var result = new Database.Models.SwarmMetadataResponse(
                    SwarmId: entity.Id,
                    Goal: entity.Goal,
                    TemplateKey: entity.TemplateKey,
                    Phase: entity.State,
                    State: entity.State,
                    IsRunning: false,
                    LockedBy: entity.LockedBy,
                    LockedAt: entity.LockedAt is { } entityLockedAt ? DateTime.SpecifyKind(entityLockedAt, DateTimeKind.Utc) : null,
                    CreatedAt: DateTime.SpecifyKind(entity.CreatedAt, DateTimeKind.Utc),
                    CompletedAt: entity.CompletedAt is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
                    Recommendation: persistedRecommendation);

                return Results.Json(result, SwarmJsonOptions.Default);
            });

        var tasksEndpoint = group.MapGet(
            "/{id:guid}/tasks",
            async (Guid id, ISwarmRepository repo) =>
            {
                var tasks = await repo.GetTasksAsync(id).ConfigureAwait(false);
                var projected = tasks.Select(Database.Mapping.SwarmTaskMapper.FromEntity);
                return Results.Json(projected, SwarmJsonOptions.Default);
            });

        var agentsEndpoint = group.MapGet(
            "/{id:guid}/agents",
            async (Guid id, ISwarmRepository repo) =>
            {
                var agents = await repo.GetAgentsAsync(id).ConfigureAwait(false);
                return Results.Json(agents, SwarmJsonOptions.Default);
            });

        var messagesEndpoint = group.MapGet(
            "/{id:guid}/messages",
            async (Guid id, ISwarmRepository repo) =>
            {
                var messages = await repo.GetMessagesAsync(id).ConfigureAwait(false);
                return Results.Json(messages, SwarmJsonOptions.Default);
            });

        var eventsEndpoint = group.MapGet(
            "/{id:guid}/events",
            async (Guid id, ISwarmRepository repo, int? limit) =>
            {
                // Default to 100 when the caller omits `?limit=`; prerequisite for the
                // Batch 3 hydration path (see swarm-list-and-session-pane.md).
                var effectiveLimit = limit ?? 100;
                var events = await repo.GetEventsAsync(id, effectiveLimit).ConfigureAwait(false);
                return Results.Json(events, SwarmJsonOptions.Default);
            });

        var templatesEndpoint = group.MapGet(
            "/templates",
            (ITemplateLoader loader) =>
            {
                var templates = loader.LoadAll();
                return Results.Json(
                    templates.Select(t => new
                    {
                        key = t.Key,
                        name = t.Name,
                        description = t.Description,
                    }),
                    SwarmJsonOptions.Default);
            });

        var listArtifactsEndpoint = group.MapGet(
            "/{id:guid}/artifacts",
            (Guid id, ISwarmManager manager) =>
            {
                var workDir = manager.GetWorkDirectory(id);
                if (workDir is null || !Directory.Exists(workDir))
                {
                    return Results.NotFound();
                }

                var entries = ArtifactProvider.ListArtifacts(workDir);
                return Results.Json(
                    new
                    {
                        files = entries.Select(e => new
                        {
                            name = e.Name,
                            path = e.Path,
                            size = e.Size,
                        }),
                    },
                    SwarmJsonOptions.Default);
            });

        var downloadZipEndpoint = group.MapGet(
            "/{id:guid}/artifacts/download-zip",
            (Guid id, ISwarmManager manager) =>
            {
                var workDir = manager.GetWorkDirectory(id);
                if (workDir is null || !Directory.Exists(workDir))
                {
                    return Results.NotFound();
                }

                var zipStream = ArtifactProvider.CreateZipArchive(workDir);
                return Results.File(zipStream, "application/zip", $"swarm-{id}.zip");
            });

        var getArtifactEndpoint = group.MapGet(
            "/{id:guid}/artifacts/{**path}",
            (Guid id, string? path, ISwarmManager manager) =>
            {
                var workDir = manager.GetWorkDirectory(id);
                if (workDir is null || !Directory.Exists(workDir))
                {
                    return Results.NotFound();
                }

                // ASP.NET routing preserves %2F literally in catch-all segments on some hosts;
                // decode explicitly so traversal attempts (e.g. "..%2Ffoo") are normalized
                // before path-security validation.
                var decoded = string.IsNullOrEmpty(path)
                    ? path
                    : Uri.UnescapeDataString(path);

                if (string.IsNullOrEmpty(decoded)
                    || !PathSecurity.TryResolveSafePath(workDir, decoded, out var resolved))
                {
                    return Results.BadRequest(new { error = "Invalid artifact path." });
                }

                if (!File.Exists(resolved))
                {
                    return Results.NotFound();
                }

                var contentType = ArtifactProvider.LooksLikeText(resolved)
                    ? "text/plain"
                    : "application/octet-stream";
                var stream = File.OpenRead(resolved);
                return Results.File(stream, contentType);
            });

        // Apply policies when enabled
        if (writePolicy is not null)
        {
            createEndpoint.RequireAuthorization(writePolicy);
            cancelEndpoint.RequireAuthorization(writePolicy);
            continueEndpoint.RequireAuthorization(writePolicy);
            smartContinueEndpoint.RequireAuthorization(writePolicy);
            skipEndpoint.RequireAuthorization(writePolicy);
            lockEndpoint.RequireAuthorization(writePolicy);
            unlockEndpoint.RequireAuthorization(writePolicy);
            markAsAwaitingInterventionEndpoint.RequireAuthorization(writePolicy);
        }

        var streamEndpoint = group.MapGet(
            "/{id:guid}/stream",
            async (Guid id, ISwarmManager manager, HttpContext httpContext) =>
            {
                // Wake an evicted swarm before we check the active-swarms
                // dictionary. Without this, any swarm past the dispatcher's
                // eviction window returns 404 and the frontend's SSE reconnect
                // loop spins forever.
                await manager.EnsureLiveAsync(id, httpContext.RequestAborted).ConfigureAwait(false);

                var execution = manager.GetSwarm(id);
                if (execution is null)
                {
                    httpContext.Response.StatusCode = 404;
                    return;
                }

                httpContext.Response.ContentType = "text/event-stream";
                httpContext.Response.Headers.CacheControl = "no-cache";
                httpContext.Response.Headers.Connection = "keep-alive";

                var reader = execution.AgUiAdapter.Reader;
                var cancellationToken = httpContext.RequestAborted;

                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(15));

                        try
                        {
                            var evt = await reader.ReadAsync(cts.Token).ConfigureAwait(false);
                            var message = SseEventWriter.FormatAgUIEvent(evt);
                            await httpContext.Response.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                            await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

                            // Stop streaming when the orchestrator has finished
                            // (success, cancel, or error). The dispatcher sets
                            // IsTerminal inside its finally block once RunAsync returns.
                            if (execution.IsTerminal)
                            {
                                break;
                            }
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            // Timeout — send heartbeat to keep the connection alive.
                            await httpContext.Response.WriteAsync(
                                SseEventWriter.FormatHeartbeat(),
                                cancellationToken).ConfigureAwait(false);
                            await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (ChannelClosedException)
                {
                    // Adapter completed — swarm finished. Normal exit.
                }
                catch (OperationCanceledException)
                {
                    // Client disconnected or request aborted. Normal exit.
                }
            });

        // Apply write policy to copilot endpoint (refinement chat modifies state)
        if (writePolicy is not null)
        {
            copilotEndpoint.RequireAuthorization(writePolicy);
        }

        // Apply read policy to read + stream endpoints
        if (readPolicy is not null)
        {
            listEndpoint.RequireAuthorization(readPolicy);
            getEndpoint.RequireAuthorization(readPolicy);
            tasksEndpoint.RequireAuthorization(readPolicy);
            agentsEndpoint.RequireAuthorization(readPolicy);
            messagesEndpoint.RequireAuthorization(readPolicy);
            eventsEndpoint.RequireAuthorization(readPolicy);
            templatesEndpoint.RequireAuthorization(readPolicy);
            streamEndpoint.RequireAuthorization(readPolicy);
            listArtifactsEndpoint.RequireAuthorization(readPolicy);
            getArtifactEndpoint.RequireAuthorization(readPolicy);
            downloadZipEndpoint.RequireAuthorization(readPolicy);
        }

        return endpoints;
    }
}
