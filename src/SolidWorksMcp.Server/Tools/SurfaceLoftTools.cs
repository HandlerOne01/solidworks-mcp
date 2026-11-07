using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("bodies")]
public sealed class SurfaceLoftTools(SwSession session)
{
    private readonly AdvancedFeatureService _svc = new(session);

    [McpServerTool(Name = "surface_loft", Title = "Lofted surface", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a native lofted surface through ordered open or closed sketches, with optional guide curves. Verifies one new surface body through the profiles and rolls back failed attempts.")]
    public Task<string> SurfaceLoft(
        IProgress<ProgressNotificationValue> progress,
        [Description("Two or more named 2D or 3D sketches in loft order, one connected contour per sketch.")] EntityRef[] profiles,
        [Description("Optional guides that meet every profile (named sketches, edges or reference curves).")] EntityRef[]? guides = null,
        [Description("Rename the new lofted surface feature.")] string? name = null,
        [Description("Keep a feature that failed verification for diagnosis.")] bool keepOnFailure = false,
        [Description("Document (handle, title or path); default: the target part.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.SurfaceLoftAsync(new SurfaceLoftRequest
        {
            Doc = doc, Profiles = profiles, Guides = guides, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "surface_loft");
}
