using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("bodies")]
public sealed class BodyScaleTools(SwSession session)
{
    private readonly BodyScaleService _scale = new(session);

    [McpServerTool(Name = "body_scale", Title = "Scale bodies", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Scale selected solid or surface bodies uniformly about the model origin with a native Scale feature. " +
                 "Verifies scaled bounds, areas, solid volumes, and the unchanged bodies. Rolls back on failure.")]
    public Task<string> BodyScale(
        IProgress<ProgressNotificationValue> progress,
        [Description("Explicit bodies to scale, e.g. [{id:'B1'}] from body_list.")] EntityRef[] bodies,
        [Description("Positive uniform scale factor, e.g. 2 doubles all lengths.")] double factor,
        [Description("Document (handle, title or path). Defaults to the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_scale.ScaleAsync(doc, bodies, factor, cancellationToken), progress, "body_scale");
}
