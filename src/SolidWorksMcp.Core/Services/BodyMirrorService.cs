using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>Creates separate reflected copies of explicit solid bodies in the same part.</summary>
public sealed class BodyMirrorService(SwSession session)
{
    private const int PlaneMark = 2;
    private const int BodiesMark = 256;
    private const string Hint = "Use solid body references from body_list and a reference plane or planar face.";
    private static readonly FeatureSpec Spec = new("body_mirror", ["MirrorSolid"], VolumeRule.Any, RequireFaces: false);

    public Task<BodyResult> MirrorAsync(string? doc, IReadOnlyList<EntityRef>? bodies, EntityRef? plane, CancellationToken ct = default)
    {
        if (bodies is not { Count: > 0 } || bodies.Any(b => b is null))
            throw SwException.InvalidArgument("'bodies' must contain at least one explicit body reference and no null entries.", Hint);
        if (plane is null) throw SwException.InvalidArgument("'plane' is required.", Hint);
        return session.RunAsync("body_mirror", ctx => Mirror(ctx, bodies, plane), RunOptions.Write(doc), ct);
    }

    private static BodyResult Mirror(SwContext ctx, IReadOnlyList<EntityRef> bodies, EntityRef plane)
    {
        var part = ctx.Part();
        var doc = (IModelDoc2)part;
        BodyKit.NoOpenSketch(doc);
        var planeRef = EntityResolver.Resolve(ctx, doc, plane, "plane", EntityKind.Plane, EntityKind.Face);
        var mirror = BodyKit.PlaneOf(planeRef) ?? throw SwException.InvalidArgument($"{planeRef.Label} is not a planar mirror reference.", Hint);
        mirror.N = mirror.N.Normalized();
        var targets = BodyKit.ResolveBodies(ctx, doc, bodies, "bodies", BodyKind.Solid);
        var targetIds = targets.Select(t => t.Id).ToHashSet();
        var originals = Measure(ctx, part, doc);
        var seeds = originals.Where(m => targetIds.Contains(m.Body.Id)).ToList();
        if (seeds.Count != targets.Count)
            throw new SwException(ErrorCodes.EntityStale, "A mirror target is absent from the current part bodies.", Hint);
        var expected = seeds.Select(m => new ExpectedCopy(m.Body,
            BodyMirrorGeometry.Bounds(dir => Extreme(m.Body.Body, dir), mirror),
            AsmPatternMath.Reflect(m.Body.Center, mirror.Q, mirror.N),
            MoveMath.Sample(m.Body.Vertices, 24).Select(p => AsmPatternMath.Reflect(p, mirror.Q, mirror.N)).ToList())).ToList();
        var resolved = targets.Select(t => t.Info("bodies")).Append(planeRef.Info("plane")).ToList();

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(doc);
        try
        {
            doc.ClearSelection2(true);
            if (!planeRef.Select(doc, false, PlaneMark))
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {planeRef.Label} as the mirror plane.", Hint);
            BodyKit.SelectAll(doc, targets, BodiesMark);
            var selection = doc.Sel();
            if (selection.GetSelectedObjectCount2(PlaneMark) != 1 || selection.GetSelectedObjectCount2(BodiesMark) != targets.Count ||
                selection.GetSelectedObjectCount2(-1) != targets.Count + 1 ||
                !Enumerable.Range(2, targets.Count).All(i => selection.BdSelected(i) is IBody2 selected &&
                    targetIds.Contains(ctx.Entities.IdFor(doc, selected, 'B', "body"))))
                throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS did not select the requested plane and mirror bodies.", Hint);

            var api = doc.FeatureManager.InsertMirrorFeature2(true, false, false, false, (int)swFeatureScope_e.swFeatureScope_AllBodies);
            doc.ClearSelection2(true);
            var copies = new List<BodyGeom>();
            var outcome = BodyKit.Verify(ctx, doc, before, Spec,
                (_, _, checks) => CheckCopies(ctx, doc, originals, Measure(ctx, part, doc), expected, copies, checks),
                keep: false, api, Hint);
            return BodyKit.Result(ctx, doc, outcome, copies, resolved);
        }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
        {
            if (BodyKit.NothingNew(doc, before)) throw;
            string rollback = FeatureVerifier.Rollback(ctx, doc, before);
            throw new SwException((ex as SwException)?.Code ?? ErrorCodes.FeatureFailed, $"{ex.Message} {rollback}", Hint, ex);
        }
        finally
        {
            doc.ClearSelection2(true);
        }
    }

    private static List<string> CheckCopies(SwContext ctx, IModelDoc2 doc, List<MeasuredBody> originals, List<MeasuredBody> after,
        List<ExpectedCopy> expected, List<BodyGeom> copies, List<string> checks)
    {
        int wantedCount = originals.Count + expected.Count;
        if (after.Count != wantedCount) return [$"expected {wantedCount} bodies, found {after.Count}"];

        foreach (var original in originals)
        {
            int index = after.FindIndex(m => SameIdentity(ctx, doc, m.Body, original.Body));
            if (index < 0 || !SameGeometry(original.Body, after[index].Body) || original.Body.Hidden != after[index].Body.Hidden ||
                !BodyMirrorGeometry.BoundsNear(original.Bounds, after[index].Bounds))
                return [$"original {original.Body.Label} changed or could not be identified"];
            after.RemoveAt(index);
        }

        foreach (var copy in expected)
        {
            int index = after.FindIndex(m => SameGeometry(copy.Seed, m.Body) && BodyMirrorGeometry.BoundsNear(copy.Bounds, m.Bounds) &&
                m.Body.Center.DistanceTo(copy.Center) <= BodyKit.CenterTol &&
                (copy.Vertices.Count == 0 || MoveMath.AllNear(copy.Vertices, m.Body.Vertices, BodyKit.VertexTol)));
            if (index < 0) return [$"no separate body matched the reflection of {copy.Seed.Label}"];
            copies.Add(after[index].Body);
            after.RemoveAt(index);
        }

        checks.Add($"{copies.Count} separate solid copies match the reflected bounds, size, and position");
        checks.Add($"{originals.Count} original bodies unchanged");
        return [];
    }

    private static bool SameIdentity(SwContext ctx, IModelDoc2 doc, BodyGeom a, BodyGeom b) =>
        a.Name == b.Name && a.Id != "?" && (a.Id == b.Id ||
            (ctx.Entities.TryGet(a.Id, out var pa) && ctx.Entities.TryGet(b.Id, out var pb) &&
             doc.Extension.IsSamePersistentID(pa.Persist, pb.Persist) == (int)swObjectEquality.swObjectSame));

    private static bool SameGeometry(BodyGeom a, BodyGeom b) =>
        a.Solid == b.Solid && a.Faces == b.Faces && a.Edges == b.Edges && BodyKit.SameSize(a, b);

    private static List<MeasuredBody> Measure(SwContext ctx, IPartDoc part, IModelDoc2 doc) =>
        BodyKit.AllGeoms(ctx, part, doc).Select(g => new MeasuredBody(g, BodyMirrorGeometry.Bounds(dir => Extreme(g.Body, dir)))).ToList();

    private static Vec3 Extreme(IBody2 body, Vec3 direction) =>
        body.GetExtremePoint(direction.X, direction.Y, direction.Z, out double x, out double y, out double z) &&
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)
            ? new Vec3(x, y, z)
            : throw new SwException(ErrorCodes.VerifyFailed, "Could not measure an exact body extremum.", Hint);

    private sealed record MeasuredBody(BodyGeom Body, double[] Bounds);
    private sealed record ExpectedCopy(BodyGeom Seed, double[] Bounds, Vec3 Center, List<Vec3> Vertices);
}

internal static class BodyMirrorGeometry
{
    private static readonly Vec3[] Axes = [Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ];

    public static double[] Bounds(Func<Vec3, Vec3> extremePoint, (Vec3 Q, Vec3 N)? mirror = null)
    {
        var bounds = new double[6];
        for (int i = 0; i < bounds.Length; i++)
        {
            var axis = Axes[i % 3];
            var direction = i < 3 ? -axis : axis;
            // An output extremum is a seed extremum along the reflected search direction.
            // Reflecting the seed's axis-aligned box can overestimate the size for oblique planes.
            var point = mirror is { } plane
                ? AsmPatternMath.Reflect(extremePoint(AsmPatternMath.ReflectDir(direction, plane.N)), plane.Q, plane.N)
                : extremePoint(direction);
            bounds[i] = point.Dot(axis);
        }
        return bounds;
    }

    public static bool BoundsNear(double[] expected, double[] actual) =>
        Enumerable.Range(0, 6).All(i => Math.Abs(expected[i] - actual[i]) <= BodyKit.PosTol);
}
