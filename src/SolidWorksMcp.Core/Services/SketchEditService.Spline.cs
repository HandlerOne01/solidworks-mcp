using System.ComponentModel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

public sealed record SplineFitPointMove
{
    [Description("Zero-based index in the spline's fitPointsMm array from sketch_info.")]
    public required int Index { get; init; }

    [Description("New coordinates in mm: [x,y] in a 2D sketch, [x,y,z] in model space for 3D.")]
    public required double[] AtMm { get; init; }
}

public sealed class SketchSplineEditResult : ToolResult
{
    public required string Sketch { get; init; }
    public required SketchSegmentInfo Spline { get; init; }
    public required string CoordinateSpace { get; init; }
    public int Moved { get; init; }
    public required string Constrained { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
    public required IReadOnlyList<ResolvedInfo> Resolved { get; init; }
}

public sealed partial class SketchEditService
{
    private const string SplineEditTool = "sketch_spline_edit";

    public Task<SketchSplineEditResult> SplineEditAsync(string? doc, EntityRef? spline, IReadOnlyList<SplineFitPointMove>? moves,
        CancellationToken ct = default)
    {
        if (spline is null) throw SwException.InvalidArgument("'spline' must name one fit-point spline.");
        if (moves is not { Count: > 0 }) throw SwException.InvalidArgument("'moves' must contain at least one fit-point move.");
        var indices = new HashSet<int>();
        var targets = new List<(int Index, Vec3 Position, int Dimensions)>(moves.Count);
        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            if (move is null || move.Index < 0)
                throw SwException.InvalidArgument($"'moves[{i}].index' must be a zero-based nonnegative fit-point index.");
            if (!indices.Add(move.Index))
                throw SwException.InvalidArgument($"Fit-point index {move.Index} is listed more than once.");
            CheckPoint(move.AtMm, $"moves[{i}].atMm", required: true);
            var at = move.AtMm;
            targets.Add((move.Index, new Vec3(Units.M(at[0]), Units.M(at[1]), at.Length == 3 ? Units.M(at[2]) : 0), at.Length));
        }
        return session.RunAsync(SplineEditTool, ctx => EditSplineFitPoints(ctx, spline, targets), RunOptions.Write(doc), ct);
    }

    private static SketchSplineEditResult EditSplineFitPoints(SwContext ctx, EntityRef spline,
        IReadOnlyList<(int Index, Vec3 Position, int Dimensions)> targets)
    {
        var doc = DocOf(ctx);
        var sketch = OpenSketch(ctx, doc, SplineEditTool, twoD: false);
        string sketchName = ((IFeature)sketch).Name;
        bool is3D = sketch.Is3D();
        if (targets.Any(t => t.Dimensions != (is3D ? 3 : 2)))
            throw SwException.InvalidArgument(is3D
                ? "A 3D spline move needs [x, y, z] in model mm."
                : "A 2D spline move needs [x, y] in sketch mm.");
        var m2s = is3D ? Xform.Identity : SketchService.SketchToModel(sketch).Inverse();
        var refs = Refs(ctx, doc, sketch, m2s, [spline], "spline", EntityKind.SketchSegment);
        if (refs.Count != 1)
            throw SwException.InvalidArgument("'spline' must resolve to one segment.");
        var reference = refs[0];
        if (reference.Obj is not ISketchSegment segment || reference.Obj is not ISketchSpline curve)
            throw SwException.InvalidArgument($"{reference.Label} is not a spline.", "Use sketch_info to find the spline segment name.");
        if (OwnerOf(segment) is not ISketch owner ||
            (!ReferenceEquals(owner, sketch) && ctx.App.IsSame((IFeature)owner, (IFeature)sketch) != (int)swObjectEquality.swObjectSame))
            throw SwException.InvalidArgument($"{reference.Label} does not belong to the open sketch.");
        string name = segment.GetName();
        if (curve.IsStyleSpline)
            throw new SwException(ErrorCodes.NotSupported, $"'{name}' is a style spline with control vertices.");
        var points = curve.SkFitPoints();
        if (points.Count < 2 || points.Count != curve.GetPointCount())
            throw new SwException(ErrorCodes.NotSupported, $"SOLIDWORKS did not expose a complete fit-point array for '{name}'.");
        foreach (var target in targets)
            if (target.Index >= points.Count)
                throw SwException.InvalidArgument($"Fit-point index {target.Index} is out of range for '{name}' (0–{points.Count - 1}).");

        var segmentId = SketchElementId((object?)segment.GetID(), name);
        var pointIds = points.Select(p => SketchElementId((object?)p.GetID(), "fit point")).ToArray();
        if (targets.Select(t => pointIds[t.Index]).Distinct().Count() != targets.Count)
            throw SwException.InvalidArgument("Two indexes identify the same fit point. Move the shared point once.");
        var original = points.Select(V).ToArray();
        var before = SkState.Take(sketch);

        List<ISketchPoint> CurrentPoints()
        {
            var active = OpenSketch(ctx, doc, SplineEditTool, twoD: false);
            var current = SameSketch(active, sketch) ? FindSegment(active, name) : null;
            if (current is not ISketchSpline found || SketchElementId((object?)current.GetID(), name) != segmentId)
                throw new SwException(ErrorCodes.VerifyFailed, $"'{name}' no longer resolves to the original spline.");
            var read = found.SkFitPoints();
            if (read.Count != pointIds.Length)
                throw new SwException(ErrorCodes.VerifyFailed, $"'{name}' changed its fit-point count.");
            return read;
        }

        ISketchPoint IndexedPoint(List<ISketchPoint> current, int index)
        {
            var point = current[index];
            if (SketchElementId((object?)point.GetID(), "fit point") != pointIds[index])
                throw new SwException(ErrorCodes.VerifyFailed, $"'{name}' no longer has its original fit point at index {index}.");
            return point;
        }

        Vec3[] ReadBack()
        {
            var current = CurrentPoints();
            if (!current.Select(p => SketchElementId((object?)p.GetID(), "fit point")).SequenceEqual(pointIds))
                throw new SwException(ErrorCodes.VerifyFailed, $"'{name}' no longer has the original ordered fit points.");
            return current.Select(V).ToArray();
        }

        string Restore()
        {
            try
            {
                // Restore only the original point objects. An in-place setter can be a no-op under
                // constraints, so a blind EditUndo2 could undo an earlier, unrelated sketch operation.
                var current = CurrentPoints();
                for (int i = original.Length - 1; i >= 0; i--)
                {
                    var point = IndexedPoint(current, i);
                    var at = original[i];
                    if (!(V(point).DistanceTo(at) < Tol))
                    {
                        point.SetCoords(at.X, at.Y, at.Z);
                        current = CurrentPoints();
                    }
                }
                var restored = SkState.Take(doc.SketchManager.ActiveSketch!);
                return SamePoints(original, ReadBack()) && Diff(before, restored).None &&
                       before.Relations == restored.Relations && before.Status == restored.Status &&
                       SamePoints(before.PointsAt, restored.PointsAt)
                    ? "Original sketch geometry verified."
                    : "Rollback incomplete. Read the open sketch with sketch_info.";
            }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
            {
                return "Rollback could not be verified. Read the open sketch with sketch_info.";
            }
        }

        SkState after;
        int moved = targets.Count(t => !(original[t.Index].DistanceTo(t.Position) < Tol));
        try
        {
            foreach (var target in targets)
            {
                var point = IndexedPoint(CurrentPoints(), target.Index);
                var at = target.Position;
                if (!(V(point).DistanceTo(at) < Tol) && !point.SetCoords(at.X, at.Y, at.Z))
                    throw new SwException(ErrorCodes.VerifyFailed, $"SOLIDWORKS refused fit-point index {target.Index}.");
            }
            var readBack = ReadBack();
            foreach (var target in targets)
                if (!(readBack[target.Index].DistanceTo(target.Position) < Tol))
                    throw new SwException(ErrorCodes.VerifyFailed, $"Fit-point index {target.Index} did not reach its requested coordinates.");
            var active = OpenSketch(ctx, doc, SplineEditTool, twoD: false);
            after = SkState.Take(active);
            var diff = Diff(before, after);
            if (diff.Created.Count != 0 || diff.Removed.Count != 0 || before.Points != after.Points || before.Relations != after.Relations ||
                BecameOverDefined(active, before.Status))
                throw new SwException(ErrorCodes.VerifyFailed, $"'{sketchName}' changed topology, relation count or solve status.");
        }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
        {
            throw new SwException(ErrorCodes.VerifyFailed, $"{ex.Message} {Restore()}",
                "Check whether sketch constraints fix the requested points.", ex);
        }

        return new SketchSplineEditResult
        {
            Doc = ctx.Title(doc),
            Sketch = sketchName,
            Spline = Info(name, after.Segs[name], is3D),
            CoordinateSpace = is3D ? "model" : "sketch",
            Moved = moved,
            Constrained = ConstrainedName(after.Status),
            Checks = [$"{targets.Count} fit-point target(s) read back", "spline and ordered point IDs retained", "segment, point and relation counts unchanged"],
            Resolved = [reference.Info("spline")],
        };
    }

    private static (int First, int Second) SketchElementId(object? value, string entity)
    {
        var id = ComArgs.Ints(value);
        if (id.Length != 2)
            throw new SwException(ErrorCodes.VerifyFailed, $"SOLIDWORKS did not return a sketch ID for {entity}.");
        return (id[0], id[1]);
    }
}
