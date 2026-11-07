using Microsoft.Extensions.Hosting;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public class SelectedBodyExportContractTests
{
    [Fact]
    public void Model_export_exposes_an_optional_body_reference()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new());
        ServerSetup.Configure(builder, fileLog: false);
        using var host = builder.Build();
        var tool = Assert.Single(ServerSetup.ListedTools(host.Services), t => t.Name == "model_export");
        var schema = tool.InputSchema;
        Assert.True(schema.GetProperty("properties").TryGetProperty("body", out var body), "model_export has no body reference");
        Assert.True(body.GetProperty("properties").TryGetProperty("id", out _));
        Assert.DoesNotContain("body", schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public void Body_scope_refuses_unrelated_formats_before_connecting()
    {
        var error = Assert.Throws<SwException>(() => ExportPlan.Parse(
            new ExportRequest { Path = "part.pdf", Body = new EntityRef { Id = "B1" } }));
        Assert.Equal(ErrorCodes.InvalidArgument, error.Code);
    }

    [Theory]
    [InlineData(1, false, true, true)]
    [InlineData(2, false, true, false)]
    [InlineData(1, true, true, false)]
    [InlineData(1, false, false, false)]
    public void Selected_step_requires_one_solid_without_surface_models_and_a_complete_trailer(
        int solids, bool surface, bool trailer, bool passes)
    {
        string step = "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('AUTOMOTIVE_DESIGN'));\nENDSEC;\nDATA;\n" +
                      string.Concat(Enumerable.Range(1, solids).Select(n => $"#{n}=MANIFOLD_SOLID_BREP('body',#10);\n")) +
                      (surface ? "#99=SHELL_BASED_SURFACE_MODEL('sheet',(#100));\n" : "") +
                      "ENDSEC;\n" + (trailer ? "END-ISO-10303-21;\n" : "");
        WithFile(step, path =>
        {
            var problems = new List<string>();
            var detail = ExportFileCheck.Inspect("step", path, new ExportFileCheck.Expect(StepAp: 214, SingleBody: true), problems);
            Assert.Equal(solids, detail.Solids);
            Assert.Equal(passes, problems.Count == 0);
        });
    }

    [Fact]
    public void Selected_stl_checks_all_dimensions_and_volume_in_mm()
    {
        WithFile(Tetrahedron(), path =>
        {
            List<string> Problems(double[] size, double volume, out ExportDetails details)
            {
                var problems = new List<string>();
                details = ExportFileCheck.Inspect("stl", path,
                    new ExportFileCheck.Expect(StlBinary: false, ModelSizeMm: size, SingleBody: true, BodyVolumeMm3: volume), problems);
                return problems;
            }
            Assert.Empty(Problems([20, 30, 40], 4000, out var details));
            Assert.Equal("mm", details.Units);
            Assert.Equal(4000, details.VolumeMm3!.Value, 0.001);
            Assert.NotEmpty(Problems([20, 20, 40], 4000, out _)); // Same largest extent, wrong Y.
            Assert.NotEmpty(Problems([20, 30, 40], 6000, out _)); // Same box, wrong volume.
            Assert.NotEmpty(Problems([20000, 30000, 40000], 4e12, out _)); // No meter inference for selected exports.
        });
    }

    [Theory]
    [InlineData("endsolid tetra\n")]
    [InlineData("endloop\n")]
    [InlineData("endfacet\n")]
    public void Selected_ascii_stl_refuses_missing_facet_records_or_footer(string missing)
    {
        string mesh = Tetrahedron();
        int offset = mesh.IndexOf(missing, StringComparison.Ordinal);
        WithFile(mesh.Remove(offset, missing.Length), path =>
        {
            var problems = new List<string>();
            ExportFileCheck.Inspect("stl", path,
                new ExportFileCheck.Expect(StlBinary: false, ModelSizeMm: [20, 30, 40], SingleBody: true, BodyVolumeMm3: 4000), problems);
            Assert.NotEmpty(problems);
        });
    }

    private static string Tetrahedron()
    {
        const string a = "0 0 0", b = "20 0 0", c = "0 30 0", d = "0 0 40";
        static string Facet(string p, string q, string r) =>
            $"facet normal 0 0 0\nouter loop\nvertex {p}\nvertex {q}\nvertex {r}\nendloop\nendfacet\n";
        return "solid tetra\n" + Facet(a, c, b) + Facet(a, b, d) + Facet(a, d, c) + Facet(b, c, d) + "endsolid tetra\n";
    }

    private static void WithFile(string contents, Action<string> check)
    {
        string path = Path.GetTempFileName();
        try { File.WriteAllText(path, contents); check(path); }
        finally { File.Delete(path); }
    }
}
