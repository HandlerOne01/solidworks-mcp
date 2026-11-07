using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;

namespace SolidWorksMcp.Core;

/// <summary>
/// Raw-API helpers for the live tests (which can't use COM types: they are embedded in this assembly only).
/// Internal; visible to SolidWorksMcp.LiveTests.
/// </summary>
internal static class TestHooks
{
    /// <summary>The allow-listed system preferences, read directly.</summary>
    public static Task<Dictionary<string, string>> PreferencesAsync(SwSession s) =>
        s.RunAsync("test_prefs", ctx =>
            SystemPrefIds.All.ToDictionary(p => $"{p.Kind}{p.Pref}", p => PreferenceScope.Read(ctx.App, p.Kind, p.Pref)));

    /// <summary>Creates a part through the raw API, bypassing the registry, so the session treats it as the user's.</summary>
    public static Task<string> NewForeignPartAsync(SwSession s) =>
        s.RunAsync("test_foreign_part", ctx =>
        {
            string tpl = ctx.App.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            var d = (object?)ctx.App.NewDocument(tpl, 0, 0, 0) as SolidWorks.Interop.sldworks.IModelDoc2
                    ?? throw new SwException(ErrorCodes.FeatureFailed, "NewDocument failed.");
            return ctx.Title(d);
        });

    /// <summary>Closes a document by title through the raw API (test cleanup only).</summary>
    public static Task CloseRawAsync(SwSession s, string title) =>
        s.RunAsync("test_close_raw", ctx =>
        {
            ctx.App.CloseDoc(title);
            return 0;
        });

    /// <summary>
    /// Sets a length dimension (mm) and rebuilds, without dimension_set's checks: a broken model on purpose. Returns
    /// SetSystemValue3's status (swSetValueReturnStatus_e) and the value read back (mm).
    /// </summary>
    public static Task<(int Status, double Mm)> SetDimensionRawAsync(SwSession s, string doc, string name, double mm) =>
        s.RunAsync("test_set_dimension_raw", ctx =>
        {
            var dim = (object?)ctx.Doc.Parameter(name) as SolidWorks.Interop.sldworks.IDimension
                      ?? throw new SwException(ErrorCodes.EntityNotFound, $"No dimension '{name}'.");
            int status = dim.SetSystemValue3(mm / 1000, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, ComArgs.Empty);
            using (var prefs = ctx.SystemPrefs())
            {
                prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
                ctx.Doc.EditRebuild3();
            }
            return (status, dim.SystemValue * 1000);
        }, RunOptions.Write(doc));

    /// <summary>The titles of all open documents.</summary>
    public static Task<List<string>> OpenTitlesAsync(SwSession s) =>
        s.RunAsync("test_titles", ctx => ctx.App.Documents().Select(ctx.Title).ToList());

    /// <summary>Optionally selects a face on an owned test document and reads the sole selected face's identity.</summary>
    public static Task<string?> SelectedFaceAsync(SwSession s, string doc, double[]? atMm = null) =>
        s.RunAsync("test_selected_face", ctx =>
        {
            var d = ctx.Doc;
            var selection = d.Sel();
            if (atMm is not null)
            {
                var face = EntityResolver.Resolve(ctx, d, new EntityRef { Type = "face", At = atMm, Strict = true }, "face", EntityKind.Face);
                d.ClearSelection2(true);
                if (!face.Select(d, append: false, mark: 0))
                    throw new SwException(ErrorCodes.FeatureFailed, "Could not select the test face.");
            }
            return selection.GetSelectedObjectCount2(-1) == 1 && (object?)selection.GetSelectedObject6(1, -1) is IFace2 selected
                ? ctx.Entities.IdFor(d, selected, 'F', "face")
                : null;
        }, atMm is null ? RunOptions.Read(doc) : RunOptions.Write(doc));
}
