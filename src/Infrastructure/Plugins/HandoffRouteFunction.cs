using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>Wraps the <c>handoff</c> tool so <c>route_keyword</c> only accepts this agent's routes.</summary>
public sealed class HandoffRouteFunction : DelegatingAIFunction
{
    private readonly JsonElement _schema;
    private readonly string _description;

    /// <summary>Creates the wrapper over the plain handoff tool.</summary>
    public HandoffRouteFunction(AIFunction inner, IReadOnlyList<HandoffRoute> routes) : base(inner)
    {
        var schema = JsonNode.Parse(inner.JsonSchema.GetRawText())!.AsObject();
        if (schema["properties"]?[HandoffPlugin.ArgumentName] is JsonObject arg)
        {
            arg["enum"] = new JsonArray(routes.Select(r => (JsonNode)JsonValue.Create(r.Name)!).ToArray());
            arg["description"] = "Where to hand off. " +
                string.Join(" ", routes.Select(r => $"\"{r.Name}\": {r.Description}"));
        }
        _schema = JsonSerializer.SerializeToElement(schema);
        _description = inner.Description;
    }

    /// <inheritdoc/>
    public override JsonElement JsonSchema => _schema;

    /// <inheritdoc/>
    public override string Description => _description;
}
