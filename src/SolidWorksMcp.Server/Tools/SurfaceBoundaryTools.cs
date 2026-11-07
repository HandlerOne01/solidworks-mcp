using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("bodies")]
public sealed class SurfaceBoundaryTools(SwSession session)
{
    private readonly AdvancedFeatureService _service = new(session);

    [McpServerTool(Name = "surface_boundary", Title = "Boundary surface", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates a native boundary surface through two pairs of opposite open sketch curves meeting at four corners. Checks the result passes through all four curves and adds one surface body. Checks the area for straight planar boundaries and rolls back failures.")]
    public Task<string> SurfaceBoundary(
        IProgress<ProgressNotificationValue> progress,
        [Description("Two named 2D or 3D sketches forming opposite sides in direction 1, in order. One open contour per sketch.")] EntityRef[] direction1,
        [Description("Two named 2D or 3D sketches forming opposite sides in direction 2, in order. Their endpoints must meet direction 1.")] EntityRef[] direction2,
        [Description("Optional name for the boundary surface feature.")] string? name = null,
        [Description("Document (handle, title or path); default: the target part.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_service.SurfaceBoundaryAsync(doc, direction1, direction2, name, cancellationToken), progress, "surface_boundary");
}
