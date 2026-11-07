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
    private const string SurfaceBoundaryTool = "surface_boundary";
    private const string SurfaceBoundaryHint = "Use four distinct sketches, each with one open chain. In each direction the two curves are opposite sides, and all four corners meet at their endpoints.";
    private static readonly FeatureSpec SurfaceBoundarySpec = new(SurfaceBoundaryTool, ["NetBlend"], VolumeRule.Any);

    public Task<BodyResult> SurfaceBoundaryAsync(string? doc, IReadOnlyList<EntityRef>? direction1,
        IReadOnlyList<EntityRef>? direction2, string? name = null, CancellationToken ct = default)
    {
        if (direction1 is not { Count: 2 } || direction2 is not { Count: 2 })
            throw SwException.InvalidArgument("'direction1' and 'direction2' each need two ordered sketch references.", SurfaceBoundaryHint);
        NoNullRefs(direction1, "direction1");
        NoNullRefs(direction2, "direction2");
        return session.RunAsync(SurfaceBoundaryTool, ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            CloseServerSketch(ctx, d, RefNames([.. direction1, .. direction2]));

            var inputs = direction1.Select((r, i) => (Reference: r, Arg: $"direction1[{i}]"))
                .Concat(direction2.Select((r, i) => (Reference: r, Arg: $"direction2[{i}]"))).ToArray();
            var refs = inputs.Select(input => EntityResolver.Resolve(ctx, d, input.Reference, input.Arg, EntityKind.Sketch)).ToArray();
            var sketches = refs.Select(r => (IFeature)r.Obj).ToArray();
            if (sketches.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
                throw SwException.InvalidArgument("A sketch cannot be used more than once in a boundary.", SurfaceBoundaryHint);

            var paths = refs.Select((r, i) => PathOf(d, r, inputs[i].Arg, requireCompleteSketch: true)).ToArray();
            if (paths.Any(p => p.Closed || p.Start is null || p.End is null))
                throw SwException.InvalidArgument("Each boundary sketch must have an open contour with two distinct endpoints.", SurfaceBoundaryHint);
            double? expectedArea = BoundaryPlanarArea(paths);
            var samples = refs.Select((r, i) => (Label: r.Label, Points: LoftProfilePoints(r, inputs[i].Arg, allowOpen: true))).ToList();
            var resolved = refs.Select((r, i) => r.Info(inputs[i].Arg)).ToList();
            var beforeGeoms = BodyKit.AllGeoms(ctx, part, d);
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            try
            {
                object? api;
                try
                {
                    // Complete sketch curves have ordered selection marks: the low bit selects the direction and the upper bits the curve index.
                    SelectAll(d, sketches.Select((sketch, i) => new Pick(refs[i].Label,
                        (model, mark) => SelectBoundarySketch(model, sketch, samples[i].Points, mark),
                        ((i % 2 + 1) << 13) | (i < 2 ? 1 : 2))).ToArray());
                    var fm = d.FeatureManager;
                    SetBoundaryDirection(fm, (short)swBoundaryBossDirection_e.swBoundaryBossDirection_First);
                    SetBoundaryDirection(fm, (short)swBoundaryBossDirection_e.swBoundaryBossDirection_Second);
                    api = fm.InsertNetBlend2(Type: 2, NCurvesDir1: 2, NCurvesDir2: 2, HasCenterline: false,
                        TessTolFactor: 0.0001, WantsSolid: false, MergeBody: true, FeatureScope: true, AutoSelect: true,
                        Thin: false, Thickness1: -1, Thickness2: -1, ReverseThickness: false, ThinSolidType: -1,
                        UseSharedThickness: false, CapEnds: false, EndThickness: -1, AutoFillet: false, FilletRadius: -1,
                        ForceNonRational: true, CreateSolid: false);
                }
                finally
                {
                    d.ClearSelection2(true);
                }

                var outcome = BodyKit.Verify(ctx, d, before, SurfaceBoundarySpec, (main, after, checks) =>
                {
                    var problems = ProfileMisses(main, samples);
                    if (after.Bodies != before.Bodies + 1 || after.SolidBodies != before.SolidBodies ||
                        Math.Abs(after.Volume - before.Volume) > 1e-13)
                        problems.Add("expected one new surface body with unchanged solid count and volume");
                    double area = main.BdFaces().Sum(face => face.GetArea());
                    if (!double.IsFinite(area) || area <= 0)
                        problems.Add("the boundary surface has no measurable area");
                    else if (expectedArea is double expected && Math.Abs(area - expected) > Math.Max(expected * 1e-4, 1e-10))
                        problems.Add($"expected planar area {F(Units.Mm2(expected))} mm² but measured {F(Units.Mm2(area))} mm²");
                    if (problems.Count == 0)
                        checks.Add($"one surface through all four input curves, area {F(Units.Mm2(area))} mm²");
                    return problems;
                }, false, api, SurfaceBoundaryHint);
                Rename(ctx, outcome.Main, name);
                string[] sketchNames = sketches.Select(s => s.Name).ToArray();
                var result = BodyKit.Result(ctx, d, outcome, BodyKit.NewOrChanged(beforeGeoms, BodyKit.AllGeoms(ctx, part, d)),
                    resolved, method: "InsertNetBlend2", consumedSketch: string.Join(", ", sketchNames));
                Consumed(ctx, sketchNames);
                return result;
            }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
            {
                BodyKit.CleanUp(ctx, d, before);
                throw;
            }
        }, RunOptions.Write(doc) with { Timeout = Long }, ct);
    }

    private static bool SelectBoundarySketch(IModelDoc2 model, IFeature sketch, IReadOnlyList<Vec3> points, int mark)
    {
        var p = points[points.Count / 2];
        return model.Extension.SelectByID2(sketch.Name, "SKETCH", p.X, p.Y, p.Z, true, mark, null, 0)
            || sketch.Select2(true, mark);
    }

    private static void SetBoundaryDirection(IFeatureManager fm, short direction)
    {
        const short noTangency = (short)swBoundaryBossTangencyType_e.swBoundaryBossTangency_None;
        fm.SetNetBlendCurveData(direction, 0, noTangency, 0, 1, true);
        fm.SetNetBlendCurveData(direction, 1, noTangency, 0, 1, true);
        fm.SetNetBlendDirectionData(direction, (short)swBoundaryBossCurveInfluenceType_e.swBoundaryBossCurve_GlobalInfluence, 0, false, false);
    }

    private static double? BoundaryPlanarArea(PathInfo[] paths)
    {
        Vec3 a = paths[0].Start!.Value, b = paths[0].End!.Value;
        Vec3 c = paths[1].Start!.Value, d = paths[1].End!.Value;
        bool Connects(PathInfo p, Vec3 x, Vec3 y) =>
            (p.Start!.Value.DistanceTo(x) <= PosTol && p.End!.Value.DistanceTo(y) <= PosTol) ||
            (p.End!.Value.DistanceTo(x) <= PosTol && p.Start!.Value.DistanceTo(y) <= PosTol);
        bool CrossCurvesMeet(Vec3 x, Vec3 y) =>
            (Connects(paths[2], a, x) && Connects(paths[3], b, y)) ||
            (Connects(paths[3], a, x) && Connects(paths[2], b, y));
        if (!CrossCurvesMeet(c, d))
        {
            (c, d) = (d, c);
            if (!CrossCurvesMeet(c, d))
                throw SwException.InvalidArgument("The two directions do not meet at four matching endpoints.", SurfaceBoundaryHint);
        }

        if (!paths.All(p => p.Straight)) return null;
        var normal = (b - a).Cross(c - a);
        if (normal.Length <= PosTol * PosTol || Math.Abs((d - a).Dot(normal.Normalized())) > PosTol) return null;
        return ((b - a).Cross(d - a) + (d - a).Cross(c - a)).Length / 2;
    }
}
