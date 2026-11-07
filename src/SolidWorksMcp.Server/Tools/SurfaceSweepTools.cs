using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("bodies")]
public sealed class SurfaceSweepTools(SwSession session)
{
    private readonly AdvancedFeatureService _service = new(session);

    [McpServerTool(Name = "surface_sweep", Title = "Swept surface", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Sweep a 2D open or closed profile along an open path. Verifies a new surface through the profile and checks span and area for a straight normal path. Failed features roll back.")]
    public Task<string> SurfaceSweep(
        IProgress<ProgressNotificationValue> progress,
        [Description("Named 2D sketch with one connected contour at an end of the path.")] EntityRef profile,
        [Description("Open path given as a sketch, edge or reference curve.")] EntityRef path,
        [Description("Optional name for the swept surface feature.")] string? name = null,
        [Description("Document (handle, title or path); default: the target part.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_service.SurfaceSweepAsync(doc, profile, path, name, cancellationToken), progress, "surface_sweep");
}
