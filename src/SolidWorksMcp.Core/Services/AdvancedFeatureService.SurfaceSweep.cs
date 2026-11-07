using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

public sealed partial class AdvancedFeatureService
{
    private const string SurfaceSweepTool = "surface_sweep";
    private const string SurfaceSweepHint = "Put a single 2D profile contour at an endpoint of an open, non-self-intersecting path.";
    private static readonly FeatureSpec SurfaceSweepSpec = new(SurfaceSweepTool, ["SweepRefSurface"], VolumeRule.Any);

    public Task<BodyResult> SurfaceSweepAsync(string? doc, EntityRef? profile, EntityRef? path, string? name = null, CancellationToken ct = default)
    {
        if (profile is null || path is null)
            throw SwException.InvalidArgument("'profile' and 'path' are required.", SurfaceSweepHint);
        return session.RunAsync(SurfaceSweepTool, ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            CloseServerSketch(ctx, d, RefNames([profile, path]));
            var profRef = EntityResolver.Resolve(ctx, d, profile, "profile", EntityKind.Sketch);
            var pathRef = EntityResolver.Resolve(ctx, d, path, "path", EntityKind.Sketch, EntityKind.Edge, EntityKind.Feature);
            var feature = (IFeature)profRef.Obj;
            if (feature.Specific() is not ISketch sketch || sketch.Is3D())
                throw SwException.InvalidArgument("'profile' must be a 2D sketch.", SurfaceSweepHint);
            var trajectory = PathOf(d, pathRef, "path", requireCompleteSketch: true);
            if (SameName(trajectory.Sketch, feature.Name))
                throw SwException.InvalidArgument("'profile' and 'path' must refer to different geometry.");
            if (trajectory.Closed || trajectory.Start is not Vec3 a || trajectory.End is not Vec3 b)
                throw SwException.InvalidArgument("'path' must be an open curve with two readable endpoints.", SurfaceSweepHint);

            var (origin, normal) = BodyKit.SketchPlane(sketch);
            double gapA = Math.Abs((a - origin).Dot(normal));
            double gapB = Math.Abs((b - origin).Dot(normal));
            if (Math.Min(gapA, gapB) > PosTol)
                throw SwException.InvalidArgument("Neither path endpoint lies in the profile plane.", SurfaceSweepHint);
            Vec3 start = gapA <= gapB ? a : b;
            Vec3 end = gapA <= gapB ? b : a;
            var points = LoftProfilePoints(profRef, "profile", allowOpen: true);
            double? expectedArea = trajectory.Straight && (end - start).IsParallelTo(normal)
                ? sketch.Segments().Where(s => !s.ConstructionGeometry).Sum(s => s.GetLength()) * trajectory.Length
                : null;
            var beforeGeoms = BodyKit.AllGeoms(ctx, part, d);
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            try
            {
                object? api;
                try
                {
                    SelectAll(d, [
                        new Pick(profRef.Label, (model, mark) => SelectSketch(model, feature, mark), ProfileMark),
                        new Pick(trajectory.Label, trajectory.Select, PathMark),
                    ]);
                    var fm = d.FeatureManager;
                    if ((object?)fm.CreateDefinition((int)swFeatureNameID_e.swFmRefSurface) is not ISweepFeatureData data)
                        throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS did not create a swept-surface definition.", SurfaceSweepHint);
                    api = fm.CreateFeature(data);
                }
                finally
                {
                    d.ClearSelection2(true);
                }

                var outcome = BodyKit.Verify(ctx, d, before, SurfaceSweepSpec, (main, after, checks) =>
                {
                    var problems = ProfileMisses(main, [(profRef.Label, points)]);
                    if (after.Bodies != before.Bodies + 1 || after.SolidBodies != before.SolidBodies)
                        problems.Add("expected one new surface body and the same solid count");
                    var faces = main.BdFaces().ToList();
                    double area = faces.Sum(f => f.GetArea());
                    if (!double.IsFinite(area) || area <= 0)
                        problems.Add("the swept surface has no measurable area");
                    if (expectedArea is double expected)
                    {
                        var (lo, hi) = BodyKit.Span(faces, start, (end - start).Normalized());
                        if (Math.Abs(lo) > PosTol || Math.Abs(hi - trajectory.Length) > PosTol ||
                            Math.Abs(area - expected) > Math.Max(expected * 1e-4, 1e-10))
                            problems.Add("the straight sweep does not span the full path with area equal to profile length × path length");
                        else checks.Add($"surface spans the full straight path and has area {Units.Mm2(area):G} mm²");
                    }
                    if (problems.Count == 0) checks.Add("one new surface passes through the profile");
                    return problems;
                }, false, api, SurfaceSweepHint);
                Consumed(ctx, feature.Name, trajectory.Sketch);
                Rename(ctx, outcome.Main, name);
                return BodyKit.Result(ctx, d, outcome, BodyKit.NewOrChanged(beforeGeoms, BodyKit.AllGeoms(ctx, part, d)),
                    [profRef.Info("profile"), pathRef.Info("path")], method: "CreateFeature(swFmRefSurface)", consumedSketch: feature.Name);
            }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
            {
                BodyKit.CleanUp(ctx, d, before);
                throw;
            }
        }, RunOptions.Write(doc) with { Timeout = Long }, ct);
    }
}
