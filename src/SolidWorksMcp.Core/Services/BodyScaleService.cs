using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>Creates a native Scale feature on explicitly referenced solid or surface bodies.</summary>
public sealed class BodyScaleService(SwSession session)
{
    private const short Origin = (short)swScaleType_e.swScaleAboutOrigin;
    private const string Hint = "Use current references from body_list and a factor within the modeler's size limits.";
    private static readonly FeatureSpec Spec = new("body_scale", ["Scale"], VolumeRule.Any, RequireFaces: false);

    public Task<BodyResult> ScaleAsync(string? doc, IReadOnlyList<EntityRef>? bodies, double factor, CancellationToken ct = default)
    {
        ValidateInputs(bodies, factor);
        var timeout = TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 120));
        return session.RunAsync("body_scale", ctx => Scale(ctx, bodies!, factor), RunOptions.Write(doc) with { Timeout = timeout }, ct);
    }

    internal static void ValidateInputs(IReadOnlyList<EntityRef>? bodies, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
            throw SwException.InvalidArgument("'factor' must be finite and greater than zero.");
        if (bodies is not { Count: > 0 } || bodies.Any(body => body is null))
            throw SwException.InvalidArgument("'bodies' must contain at least one explicit body reference and no null entries.");
    }

    private static BodyResult Scale(SwContext ctx, IReadOnlyList<EntityRef> bodies, double factor)
    {
        var part = ctx.Part();
        var doc = (IModelDoc2)part;
        BodyKit.NoOpenSketch(doc);
        var targets = BodyKit.ResolveBodies(ctx, doc, bodies, "bodies", BodyKind.Any);
        var original = Measure(ctx, part, doc);

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(doc);
        try
        {
            return Insert(ctx, part, doc, before, original, targets, factor, mark: 1);
        }
        catch (SwException ex) when (ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.VerifyFailed)
        {
            // InsertScale's documented signature does not specify a body selection mark. Retry with
            // unmarked selections only after confirming that the first attempt left the part intact.
            if (!BodyKit.NothingNew(doc, before)) throw;
            var restored = Measure(ctx, part, doc);
            if (!SameBodies(ctx, doc, original, restored)) throw;
            targets = BodyKit.ResolveBodies(ctx, doc, bodies, "bodies", BodyKind.Any);
            return Insert(ctx, part, doc, before, restored, targets, factor, mark: 0);
        }
    }

    private static BodyResult Insert(SwContext ctx, IPartDoc part, IModelDoc2 doc, Snapshot before, List<MeasuredBody> original,
        List<Resolved> targets, double factor, int mark)
    {
        var result = new List<BodyGeom>();
        var targetIds = targets.Select(t => t.Id).ToHashSet();
        var inputs = original.Where(b => targetIds.Contains(b.Body.Id)).ToList();
        if (inputs.Count != targets.Count)
            throw new SwException(ErrorCodes.EntityStale, "A scale target is absent from the current part bodies.", Hint);
        var resolved = targets.Select(t => t.Info("bodies")).ToList();
        try
        {
            doc.ClearSelection2(true);
            BodyKit.SelectAll(doc, targets, mark);
            var selection = doc.Sel();
            if (selection.GetSelectedObjectCount2(mark) != targets.Count ||
                !Enumerable.Range(1, targets.Count).All(i => selection.BdSelected(i) is IBody2 body &&
                    targetIds.Contains(ctx.Entities.IdFor(doc, body, 'B', "body"))))
                throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS did not select the requested scale bodies.", Hint);
            var api = doc.FeatureManager.InsertScale(Origin, true, factor, factor, factor);
            doc.ClearSelection2(true);
            var outcome = BodyKit.Verify(ctx, doc, before, Spec, (feature, _, checks) =>
            {
                if ((object?)feature.GetDefinition() is not IScaleFeatureData data || data.Type != Origin || !data.IsUniform ||
                    !BodyScaleGeometry.Near(data.ScaleUniform, factor))
                    return ["the Scale definition does not have the requested uniform factor and model origin"];

                var remaining = Measure(ctx, part, doc);
                if (remaining.Count != original.Count) return ["the number of bodies changed"];

                foreach (var body in original.Where(b => !targetIds.Contains(b.Body.Id)))
                {
                    int index = remaining.FindIndex(b => Unchanged(ctx, doc, body, b));
                    if (index < 0)
                        return [$"unselected {body.Body.Label} changed or could not be identified"];
                    remaining.RemoveAt(index);
                }

                foreach (var body in inputs)
                {
                    int index = remaining.FindIndex(b => body.Shape.Matches(b.Shape, factor));
                    if (index < 0) return [$"{body.Body.Label} does not match the expected scaled bounds, area, or solid volume"];
                    result.Add(remaining[index].Body);
                    remaining.RemoveAt(index);
                }

                checks.Add($"{result.Count} bodies scaled uniformly about the model origin by {factor:G}");
                checks.Add($"{original.Count - result.Count} unselected bodies unchanged");
                return [];
            }, keep: false, api, Hint);
            return BodyKit.Result(ctx, doc, outcome, result, resolved);
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

    private static List<MeasuredBody> Measure(SwContext ctx, IPartDoc part, IModelDoc2 doc) =>
        BodyKit.AllGeoms(ctx, part, doc).Select(b => new MeasuredBody(b,
            new BodyScaleGeometry(b.Solid, b.Area, b.Volume, ExactBounds(b.Body), b.Faces, b.Edges))).ToList();

    private static bool SameBodies(SwContext ctx, IModelDoc2 doc, List<MeasuredBody> original, List<MeasuredBody> current) =>
        original.Count == current.Count && original.All(a => current.Any(b => Unchanged(ctx, doc, a, b)));

    private static bool Unchanged(SwContext ctx, IModelDoc2 doc, MeasuredBody a, MeasuredBody b) =>
        a.Body.Name == b.Body.Name && a.Body.Hidden == b.Body.Hidden && a.Shape.Matches(b.Shape, 1) &&
        SameBody(ctx, doc, a.Body.Id, b.Body.Id);

    private static bool SameBody(SwContext ctx, IModelDoc2 doc, string first, string second) =>
        first != "?" && (first == second || (ctx.Entities.TryGet(first, out var a) && ctx.Entities.TryGet(second, out var b) &&
            doc.Extension.IsSamePersistentID(a.Persist, b.Persist) == (int)swObjectEquality.swObjectSame));

    private static double[] ExactBounds(IBody2 body) =>
        body.GetExtremePoint(-1, 0, 0, out double minX, out _, out _) &&
        body.GetExtremePoint(0, -1, 0, out _, out double minY, out _) &&
        body.GetExtremePoint(0, 0, -1, out _, out _, out double minZ) &&
        body.GetExtremePoint(1, 0, 0, out double maxX, out _, out _) &&
        body.GetExtremePoint(0, 1, 0, out _, out double maxY, out _) &&
        body.GetExtremePoint(0, 0, 1, out _, out _, out double maxZ)
            ? [minX, minY, minZ, maxX, maxY, maxZ]
            : throw new SwException(ErrorCodes.VerifyFailed, "Could not measure the exact bounds of a part body.", Hint);

    private sealed record MeasuredBody(BodyGeom Body, BodyScaleGeometry Shape);
}

internal sealed record BodyScaleGeometry(bool Solid, double Area, double Volume, double[] Bounds, int Faces, int Edges)
{
    public bool Matches(BodyScaleGeometry other, double factor) =>
        Solid == other.Solid && Faces == other.Faces && Edges == other.Edges &&
        Near(other.Area, Area * factor * factor) &&
        (!Solid || Near(other.Volume, Volume * factor * factor * factor)) &&
        Enumerable.Range(0, 6).All(i => double.IsFinite(Bounds[i] * factor) &&
            Math.Abs(other.Bounds[i] - Bounds[i] * factor) <= 1e-8);

    public static bool Near(double actual, double expected) =>
        double.IsFinite(actual) && double.IsFinite(expected) && actual > 0 && expected > 0 &&
        Math.Abs(actual - expected) <= 1e-6 * expected;
}
