using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.AI;

namespace Swarmwright.Tools;

/// <summary>
/// Base class for custom tool providers. Subclasses decorate public instance methods
/// with <see cref="SwarmToolAttribute"/>; this class reflects over those methods at
/// construction time and wraps each one as an <see cref="AITool"/> via
/// <see cref="AIFunctionFactory.Create(Delegate, string?, string?, System.Text.Json.JsonSerializerOptions?)"/>.
/// Results are cached, so <see cref="GetTools"/> always returns the same list.
/// </summary>
public abstract class CustomToolProvider : ICustomToolProvider
{
    private readonly Lazy<IReadOnlyList<AITool>> cachedTools;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomToolProvider"/> class.
    /// </summary>
    protected CustomToolProvider()
    {
        this.cachedTools = new Lazy<IReadOnlyList<AITool>>(this.DiscoverTools);
    }

    /// <inheritdoc/>
    public IReadOnlyList<AITool> GetTools() => this.cachedTools.Value;

    private List<AITool> DiscoverTools()
    {
        var tools = new List<AITool>();

        var methods = this.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<SwarmToolAttribute>();
            if (attr is null)
            {
                continue;
            }

            var delegateType = BuildDelegateType(method);
            var del = Delegate.CreateDelegate(delegateType, this, method);
            tools.Add(AIFunctionFactory.Create(del, name: attr.Name, description: attr.Description));
        }

        return tools;
    }

    private static Type BuildDelegateType(MethodInfo method)
    {
        var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();

        if (method.ReturnType == typeof(void))
        {
            return paramTypes.Length == 0
                ? typeof(Action)
                : Expression.GetActionType(paramTypes);
        }

        var funcTypes = new Type[paramTypes.Length + 1];
        Array.Copy(paramTypes, funcTypes, paramTypes.Length);
        funcTypes[paramTypes.Length] = method.ReturnType;
        return Expression.GetFuncType(funcTypes);
    }
}
