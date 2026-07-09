# Swarm Custom Tools

How to write domain-specific tools (database queries, HTTP APIs, business logic) that your swarm workers can call, without forking the framework.

For template authoring fundamentals (frontmatter, workers, skills), see [template.md](template.md). For app setup, see [swarm.md](swarm.md).

---

## 1. Quick Start

1. Subclass `CustomToolProvider`, add a constructor for your dependencies.
2. Decorate each method you want to expose with `[SwarmTool("name", "description")]`.
3. Declare the DI lifetime at the class level with `[SwarmToolProvider(ServiceLifetime.X)]` — default is `Transient`.
4. List the tool name in your worker's `custom_tools:` frontmatter.

That's it. `AddAISwarm` auto-discovers your provider class — no manual DI registration needed.

```csharp
// DatabaseTools.cs
using Swarmwright.Tools;

[SwarmToolProvider(ServiceLifetime.Scoped)]   // DbContext is scoped
public sealed class DatabaseTools : CustomToolProvider
{
    private readonly MyDbContext db;
    public DatabaseTools(MyDbContext db) { this.db = db; }

    [SwarmTool("query_customers", "Find customers by name fragment.")]
    public async Task<string> QueryCustomersAsync(
        [Description("Name fragment to match (case-insensitive).")] string fragment,
        [Description("Max rows to return.")] int limit = 25)
    {
        var rows = await this.db.Customers
            .Where(c => c.Name.Contains(fragment))
            .Take(limit)
            .ToListAsync();
        return JsonSerializer.Serialize(rows);
    }
}
```

```yaml
# worker-analyst.md
---
name: analyst
displayName: Data Analyst
description: Answers questions about customers
custom_tools:
  - query_customers
---

# Analyst

You are a data analyst...
```

---

## 2. Defining a provider

The custom-tool types live in the `Swarmwright.Tools` namespace. The interface and the run-context
contract ship in `Swarmwright.Abstractions`; the reflection base class ships in the core `Swarmwright`
package.

### Base class vs. interface

- **`CustomToolProvider`** (base class) — recommended. You write regular C# methods decorated with `[SwarmTool]`; the base class reflects over them at construction time and wraps each as an `AIFunction` via `Microsoft.Extensions.AI.AIFunctionFactory.Create`. The result is cached, so `GetTools()` always returns the same list.
- **`ICustomToolProvider`** (interface) — for exotic cases where you need full control over how `AITool` instances are built (e.g., tools whose schema is generated dynamically from a config file). Implement `GetTools()` yourself; it returns an `IReadOnlyList<AITool>`.

Most consumers want the base class. Only reach for `ICustomToolProvider` directly when you're doing something the attribute-based flow can't express.

### The `[SwarmTool]` attribute

```csharp
[SwarmTool("query_customers", "Find customers by name fragment.")]
```

- **First argument (name)** — the tool identifier the LLM sees and the value consumers put in `custom_tools:` frontmatter. Use `snake_case` for consistency with framework tools (`task_update`, `inbox_send`, `web_fetch`).
- **Second argument (description)** — shown to the LLM. Be specific: "Find customers by name fragment" is useful; "Query the database" is not. The model uses this to decide when to call the tool.

Both arguments are required — tool identity is stable under method renames, and the description shown to the model is rich enough to be useful.

Tool names are the **stable identity** — if you rename the method `QueryCustomersAsync` → `FindCustomersAsync`, the `[SwarmTool("query_customers", ...)]` attribute keeps the tool name the same, so existing templates keep working.

### Parameter descriptions

Use `System.ComponentModel.Description` on each parameter the LLM should understand:

```csharp
public async Task<string> QueryCustomersAsync(
    [Description("Name fragment to match (case-insensitive).")] string fragment,
    [Description("Max rows to return.")] int limit = 25)
```

The framework passes these through the `Microsoft.Extensions.AI` schema generation so the LLM sees a structured parameter schema with your descriptions. Default values (`= 25`) are respected.

### Async methods

`Task<T>` and `ValueTask<T>` are fully supported — just write async methods. The framework unwraps the task automatically before returning the result to the LLM.

### Return values

Return `string`, `object`, or any serializable type. The framework serializes return values as JSON for the LLM. For structured data, returning a concrete DTO (not `object`) gives the LLM a stable schema.

---

## 3. Dependency injection

Provider classes are regular DI-managed services — constructor-inject anything from the container. The `[SwarmToolProvider(ServiceLifetime.X)]` attribute controls the lifetime.

### Choosing a lifetime

| Lifetime | Use when | Example dependencies |
|----------|----------|----------------------|
| **Transient** (default) | Your provider has lightweight state and safe-to-recreate dependencies. Safest choice. | Pure logic, stateless HTTP calls via `IHttpClientFactory`. |
| **Scoped** | Your provider depends on scoped services. Each swarm run gets its own instance. | `DbContext`, `IHttpContextAccessor`, scoped repositories. |
| **Singleton** | Your provider and all its dependencies are thread-safe and stateless. | In-memory caches, pre-computed lookup tables, pure compute. |

**When in doubt, default to Transient.** A singleton that takes a scoped dependency causes a captive-dependency runtime error; a transient never does.

Example with scoped dependency:

```csharp
[SwarmToolProvider(ServiceLifetime.Scoped)]
public sealed class DatabaseTools : CustomToolProvider
{
    private readonly MyDbContext db;     // scoped service
    public DatabaseTools(MyDbContext db) { this.db = db; }
    // ...
}
```

Example with shared HttpClient:

```csharp
public sealed class StripeTools : CustomToolProvider   // no attribute → Transient
{
    private readonly HttpClient http;
    public StripeTools(IHttpClientFactory factory)
    {
        this.http = factory.CreateClient("stripe");
    }
    // ...
}
```

### Per-swarm run context (`ISwarmRunContext`)

A provider often needs to know *which swarm run it is serving* — the run's id, its work directory, or
caller-supplied parameters. Inject the read-only **`ISwarmRunContext`** to get them:

```csharp
public interface ISwarmRunContext
{
    Guid SwarmId { get; }                                 // which swarm this run is
    string WorkDirectory { get; }                         // that run's isolated work dir
    IReadOnlyDictionary<string, string> Context { get; }  // free-form metadata from creation
}
```

The dispatcher populates the context for each run **before** the worker agents (and their tools) are built, so
any provider resolved from the per-swarm scope observes the values for that run. **Both `Transient` (default) and
`Scoped` providers work** — they're resolved from the per-swarm scope either way, so you do *not* need to mark a
provider `Scoped` just to read the context.

```csharp
public sealed class SourceFileTools : CustomToolProvider
{
    private readonly string sourceRoot;

    public SourceFileTools(ISwarmRunContext run)
    {
        // e.g. a code-review swarm created with context = { "sourceRoot": "<clone path>" }
        this.sourceRoot = run.Context.TryGetValue("sourceRoot", out var root) ? root : run.WorkDirectory;
    }

    [SwarmTool("read_source_file", "Read a file from the PR's source clone.")]
    public async Task<string> ReadSourceFileAsync(
        [Description("Repo-relative path to read.")] string path)
    {
        // Resolve `path` against this.sourceRoot using the same path-security guard the default
        // file tools use — a separate, explicitly-rooted sandbox, not a hole in workdir confinement.
        ...
    }
}
```

**Supplying context at creation.** All three create surfaces accept an optional context bag; it defaults to empty,
so existing callers that omit it are unaffected:

| Surface | How |
|---|---|
| Workflow (`SwarmExecutor`) | `SwarmInvocationInput.New(goal, templateKey, context)` |
| HTTP | `POST /api/swarm` body `{ "goal": "...", "templateKey": "...", "context": { "sourceRoot": "..." } }` |
| MCP | `create_swarm(goal, templateKey?, context?)` |

The context is **persisted with the swarm row**, so it survives eviction and is restored on resume — a tool that
reads it after a resume sees the same values. Keep it small: it's string/string metadata (paths, ids, flags), not
a channel for large payloads. A malformed/blank persisted value degrades to an empty `Context` (logged), never a
crash.

---

## 4. Registration — you don't have to do it

Once you've defined a provider class, `AddAISwarm` picks it up automatically:

```csharp
// Program.cs
builder.Services.AddAISwarm(builder.Configuration, builder.Environment);
//      ↑ scans loaded assemblies for ICustomToolProvider implementations
//        and registers each one with its attribute-declared lifetime.
```

No `services.AddScoped<ICustomToolProvider, DatabaseTools>()` call. No forgotten DI step.

### When you want manual control

This only applies if you want total control of the registration. Pass `discoverCustomToolProviders: false` to opt out of auto-discovery:

```csharp
builder.Services.AddAISwarm(builder.Configuration, builder.Environment, discoverCustomToolProviders: false);
builder.Services.AddScoped<ICustomToolProvider, DatabaseTools>();   // you register explicitly
```

Useful for:
- Conditional registration (`if (enableExperimentalTools) { ... }`)
- Test isolation — registering a mock `ICustomToolProvider` without scan interference
- Advanced lifetime control (e.g., keyed services, factories)

### Manual registration always wins

Even with discovery enabled, if you've already registered a specific type manually, discovery skips it. So this works:

```csharp
// Register manually FIRST to pre-empt the scan:
builder.Services.AddSingleton<ICustomToolProvider, DatabaseTools>();   // override the attribute's lifetime
builder.Services.AddAISwarm(...);                                      // discovery sees the pre-registration and skips
```

Register *before* `AddAISwarm`. If you register the same type *after*, the discovery scan has already
run and both registrations end up in the container.

The rule: discovery checks `services.Any(sd => sd.ServiceType == typeof(ICustomToolProvider) && sd.ImplementationType == type)` before adding, and respects any existing registration.

---

## 5. Worker opt-in

Workers declare which custom tools they're allowed to use in their frontmatter:

```yaml
---
name: analyst
displayName: Data Analyst
description: Answers customer questions
custom_tools:
  - query_customers
  - run_sales_report
---
```

**The framework only injects tools whose names match `custom_tools:`.** This is an allowlist, not a catalog — if you have five providers registered with 20 tools total and a worker lists only `query_customers`, they only see that one tool.

### Why allowlist?

- **Least-privilege by default.** An Azure architecture worker shouldn't see customer-DB tools.
- **Auditable.** A reviewer can look at the `.md` file and see exactly what the worker has access to.
- **Stable naming.** Workers reference tools by name; renaming a method in the provider doesn't break workers because `[SwarmTool("name", ...)]` keeps the name fixed.

### Multiple providers, one worker

If a worker lists `custom_tools: [query_db, send_slack]` and the tools live in different providers (`DatabaseTools` and `SlackTools`), both providers are queried and matching tools from each end up in the worker's tool list. Order doesn't matter.

---

## 6. Testing patterns

### Unit testing a provider

Your provider is a plain class — construct it directly:

```csharp
[TestMethod]
public async Task QueryCustomers_ReturnsRowsMatchingFragment()
{
    await using var db = new MyDbContext(InMemoryOptions);
    db.Customers.Add(new Customer { Name = "Contoso" });
    db.Customers.Add(new Customer { Name = "Fabrikam" });
    await db.SaveChangesAsync();

    var tools = new DatabaseTools(db);

    var aiFunction = tools.GetTools().OfType<AIFunction>().First(t => t.Name == "query_customers");
    var result = await aiFunction.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
    {
        ["fragment"] = "Cont",
        ["limit"] = 10,
    }));

    result!.ToString().Should().Contain("Contoso");
    result.ToString().Should().NotContain("Fabrikam");
}
```

### Integration testing with the orchestrator

Pass an instance (or list) of `ICustomToolProvider` directly to the `SwarmOrchestrator` constructor — the last constructor parameter is an optional `IEnumerable<ICustomToolProvider>?`. See [`SwarmOrchestratorCustomToolsTests.cs`](../tests/Swarmwright.Tests/Orchestration/SwarmOrchestratorCustomToolsTests.cs) for the full pattern with mock chat clients and temp template directories.

### Avoiding discovery in tests

Test assemblies often contain private provider classes that happen to implement `ICustomToolProvider`. If those get auto-registered in unrelated integration tests, you can scope them out:

- Note that defining test providers as nested private classes does NOT hide them from the scan — reflection still discovers them.
- Use `discoverCustomToolProviders: false` in integration tests and register exactly the providers the test needs.

---

## 7. Troubleshooting

### "My tool isn't showing up"

1. **Did you add `custom_tools: [my_tool]` to the worker's frontmatter?** Without this, the allowlist is empty and no custom tools are injected.
2. **Does the `[SwarmTool("my_tool", ...)]` name match the frontmatter name exactly?** Names are case-sensitive ordinal compare.
3. **Is the provider registered?** Run with debug logs — the orchestrator logs `Custom tool '{ToolName}' requested by worker '{WorkerName}' is not supplied by any registered ICustomToolProvider.` when a `custom_tools` entry matches nothing.
4. **Are you calling `AddAISwarm` before the provider's assembly loads?** Auto-discovery scans `AppDomain.CurrentDomain.GetAssemblies()` — if your provider lives in an assembly that isn't loaded at the time of the scan, it's missed. In practice this only matters with lazy-loaded plugins; standard project references are always loaded at startup.

### "I get `InvalidOperationException: Cannot consume scoped service ... from singleton ...`"

Captive dependency. Either:
- Change your provider's lifetime to `Scoped` or `Transient` (via `[SwarmToolProvider(ServiceLifetime.Scoped)]`)
- Or redesign so the provider takes `IServiceScopeFactory` / `IHttpClientFactory` instead of the scoped service directly

### "Two tools have the same name"

Last-registration-wins in iteration order. Rename one `[SwarmTool]` to a unique name. The LLM sees tool names as unique identifiers — duplicates cause undefined behavior upstream.

---

## 8. References

- Framework interface: [`ICustomToolProvider`](../src/Swarmwright.Abstractions/Tools/ICustomToolProvider.cs)
- Base class: [`CustomToolProvider`](../src/Swarmwright/Tools/CustomToolProvider.cs)
- Attributes: [`SwarmToolAttribute`](../src/Swarmwright.Abstractions/Tools/SwarmToolAttribute.cs), [`SwarmToolProviderAttribute`](../src/Swarmwright.Abstractions/Tools/SwarmToolProviderAttribute.cs)
- Run context: [`ISwarmRunContext`](../src/Swarmwright.Abstractions/Tools/ISwarmRunContext.cs)
- Discovery pipeline: [`SwarmServiceExtensions.DiscoverCustomToolProviders`](../src/Swarmwright/Extensions/SwarmServiceExtensions.cs)
- `AddAISwarm`: [`IServiceCollectionExtensions`](../src/Swarmwright.AspNetCore/Extensions/IServiceCollectionExtensions.cs)
- Template authoring: [template.md](template.md)
