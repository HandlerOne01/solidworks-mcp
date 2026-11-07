using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("bodies")]
public sealed class BodyMirrorTools(SwSession session)
{
    private readonly BodyMirrorService _mirror = new(session);

    [McpServerTool(Name = "body_mirror", Title = "Mirror solid bodies", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Mirror / reflect selected solid bodies as separate copies in the same part about a reference plane or planar face. " +
                 "Verifies the reflected geometry and preserves the originals. Rolls back on failure.")]
    public Task<string> BodyMirror(
        IProgress<ProgressNotificationValue> progress,
        [Description("Solid bodies to copy, e.g. [{id:'B1'}] from body_list.")] EntityRef[] bodies,
        [Description("A reference plane, e.g. {name:'right'}, or a planar face.")] EntityRef plane,
        [Description("Document (handle, title or path). Defaults to the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_mirror.MirrorAsync(doc, bodies, plane, cancellationToken), progress, "body_mirror");
}
