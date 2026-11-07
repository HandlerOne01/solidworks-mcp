using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Files;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;

namespace SolidWorksMcp.Core.Services;

/// <summary>model_export arguments (validated again here; the tool layer only binds them).</summary>
public sealed record ExportRequest
{
    public required string Path { get; init; }
    /// <summary>step | iges | parasolid | stl | 3mf | png | jpg | eprt | pdf | dwg | dxf (aliases: stp, igs, x_t, jpeg, easm, edrw); null = from the extension.</summary>
    public string? Format { get; init; }
    public int StepAp { get; init; } = 214;
    public bool StlBinary { get; init; } = true;
    /// <summary>coarse | fine | custom.</summary>
    public string StlQuality { get; init; } = "fine";
    public double? StlDeviationMm { get; init; }
    public double? StlAngleDeg { get; init; }
    /// <summary>Export just this visible part solid to STEP or STL.</summary>
    public EntityRef? Body { get; init; }
    public bool Overwrite { get; init; }
    public string? Doc { get; init; }
}

/// <summary>What the written file was checked for (only the fields that apply to its format are set).</summary>
public sealed class ExportDetails
{
    /// <summary>The signature that was found, e.g. "ISO-10303-21", "STL (binary)", "PNG".</summary>
    public string? Header { get; set; }
    /// <summary>STEP FILE_SCHEMA, e.g. AUTOMOTIVE_DESIGN.</summary>
    public string? Schema { get; set; }
    /// <summary>STEP application protocol read from the schema (203, 214, 242).</summary>
    public int? StepAp { get; set; }
    /// <summary>STEP: B-rep solids in the file (MANIFOLD_SOLID_BREP / BREP_WITH_VOIDS).</summary>
    public int? Solids { get; set; }
    /// <summary>STEP: surface models (SHELL_BASED_SURFACE_MODEL).</summary>
    public int? SurfaceModels { get; set; }
    /// <summary>STL: binary (true) or ASCII.</summary>
    public bool? Binary { get; set; }
    /// <summary>STL / 3MF triangle count.</summary>
    public long? Triangles { get; set; }
    /// <summary>STL: length unit of the coordinates (inferred from the model box); 3MF: the model's unit attribute.</summary>
    public string? Units { get; set; }
    /// <summary>STL: enclosed mesh volume in mm³ (when the unit is known).</summary>
    public double? VolumeMm3 { get; set; }
    /// <summary>STL: mesh extents in mm (when the unit is known).</summary>
    public double[]? SizeMm { get; set; }
    /// <summary>STL: mesh extents in the file's own units (when the unit could not be inferred).</summary>
    public double[]? MeshSize { get; set; }
    /// <summary>STL: enclosed volume in the file's own units (when the unit could not be inferred).</summary>
    public double? MeshVolume { get; set; }
    public int? WidthPx { get; set; }
    public int? HeightPx { get; set; }
    /// <summary>PDF version, DWG/DXF release, Parasolid schema.</summary>
    public string? Version { get; set; }
    /// <summary>IGES: entity count from the terminate section.</summary>
    public int? Entities { get; set; }
    /// <summary>The checks that passed.</summary>
    public IReadOnlyList<string>? Checks { get; set; }
}

public sealed class ExportResult : ToolResult
{
    /// <summary>The file actually written (SOLIDWORKS may change the extension's case, e.g. .STL).</summary>
    public required string Path { get; init; }
    public required string Format { get; init; }
    public long SizeBytes { get; init; }
    /// <summary>The export options that were in effect (system options are restored afterwards).</summary>
    public IReadOnlyList<string>? Settings { get; init; }
    public required ExportDetails Details { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
    /// <summary>True when an existing file was replaced (overwrite=true).</summary>
    public bool? Replaced { get; init; }
    /// <summary>Copy of the replaced file when this server hadn't written it.</summary>
    public string? BackupPath { get; init; }
}

/// <summary>Document kinds an export format applies to.</summary>
[Flags]
internal enum ExportDocs
{
    Part = 1,
    Assembly = 2,
    Drawing = 4,
    Model = Part | Assembly,
    Any = Part | Assembly | Drawing,
}

/// <summary>One export format: canonical name, file extensions (first = default) and the documents it applies to.</summary>
internal sealed record ExportFormat(string Name, string[] Extensions, ExportDocs Docs, string Label);

/// <summary>The formats model_export writes. SOLIDWORKS picks the translator from the file extension of SaveAs.</summary>
internal static class ExportFormats
{
    public static readonly IReadOnlyList<ExportFormat> All =
    [
        new("step", [".step", ".stp"], ExportDocs.Model, "STEP"),
        new("iges", [".igs", ".iges"], ExportDocs.Model, "IGES"),
        new("parasolid", [".x_t"], ExportDocs.Model, "Parasolid"),
        new("stl", [".stl"], ExportDocs.Model, "STL"),
        new("3mf", [".3mf"], ExportDocs.Model, "3MF"),
        new("png", [".png"], ExportDocs.Any, "PNG"),
        new("jpg", [".jpg", ".jpeg"], ExportDocs.Any, "JPEG"),
        new("edrawings", [".eprt", ".easm", ".edrw"], ExportDocs.Any, "eDrawings"),
        new("pdf", [".pdf"], ExportDocs.Any, "PDF"),
        new("dwg", [".dwg"], ExportDocs.Drawing, "DWG"),
        new("dxf", [".dxf"], ExportDocs.Drawing, "DXF"),
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stp"] = "step", ["igs"] = "iges", ["x_t"] = "parasolid", ["xt"] = "parasolid", ["jpeg"] = "jpg",
        ["eprt"] = "edrawings", ["easm"] = "edrawings", ["edrw"] = "edrawings",
    };

    public const string Names = "step, iges, parasolid (x_t), stl, 3mf, png, jpg, eprt, pdf, dwg, dxf";
    public const string ExtensionList = ".step .stp .igs .iges .x_t .stl .3mf .png .jpg .jpeg .eprt .easm .edrw .pdf .dwg .dxf";

    public static ExportFormat? ByName(string name)
    {
        string n = name.Trim().TrimStart('.').ToLowerInvariant();
        if (Aliases.TryGetValue(n, out var a)) n = a;
        return All.FirstOrDefault(f => f.Name == n);
    }

    public static ExportFormat? ByExtension(string extension)
    {
        string e = extension.ToLowerInvariant();
        return e.Length == 0 ? null : All.FirstOrDefault(f => f.Extensions.Contains(e));
    }

    public static string Describe(ExportDocs docs) => docs switch
    {
        ExportDocs.Model => "part or assembly",
        ExportDocs.Drawing => "drawing",
        ExportDocs.Part => "part",
        ExportDocs.Assembly => "assembly",
        _ => "document",
    };
}

/// <summary>The COM-free part of an export request: format and option validation (before anything is queued).</summary>
internal sealed record ExportPlan(ExportFormat Format, string Quality, bool PathHasFormatExtension)
{
    public static ExportPlan Parse(ExportRequest q)
    {
        if (string.IsNullOrWhiteSpace(q.Path))
            throw SwException.InvalidArgument("'path' is empty.", "Pass a file such as plate.step (relative paths go under the output folder).");
        string p = q.Path.Trim().Trim('"');
        string name = System.IO.Path.GetFileName(p);
        if (name.Length == 0) throw SwException.InvalidArgument($"'path' must name a file, not a folder ('{p}').");
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw SwException.InvalidArgument($"'path' contains characters that file names can't have ('{name}').");

        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        var byExt = ExportFormats.ByExtension(ext);
        ExportFormat? byName = null;
        if (!string.IsNullOrWhiteSpace(q.Format))
            byName = ExportFormats.ByName(q.Format!)
                     ?? throw SwException.InvalidArgument($"Unknown export format '{q.Format}'.", $"Use {ExportFormats.Names}.");
        var format = byName ?? byExt
                     ?? throw SwException.InvalidArgument(
                         ext.Length == 0 ? $"'{name}' has no extension, so the export format is unknown." : $"'{ext}' is not an export format.",
                         $"Use one of {ExportFormats.ExtensionList}, or pass 'format'.");
        if (byName is not null && byExt is not null && byName != byExt)
            throw SwException.InvalidArgument($"'path' ends in '{ext}' ({byExt.Label}) but format is '{q.Format}' ({byName.Label}).",
                "Drop 'format' (the extension decides) or change the extension.");

        if (format.Name == "step" && q.StepAp is not (203 or 214 or 242))
            throw SwException.InvalidArgument($"stepAp must be 203, 214 or 242 (got {q.StepAp}).", "214 (AUTOMOTIVE_DESIGN) is the most widely read.");
        if (q.Body is not null && format.Name is not ("step" or "stl"))
            throw SwException.InvalidArgument("'body' is available for STL and STEP exports.");

        string quality = (q.StlQuality ?? "fine").Trim().ToLowerInvariant();
        if (format.Name == "stl")
        {
            if (quality is not ("coarse" or "fine" or "custom"))
                throw SwException.InvalidArgument($"stlQuality must be coarse, fine or custom (got '{q.StlQuality}').");
            if ((q.StlDeviationMm is not null || q.StlAngleDeg is not null) && quality != "custom")
                throw SwException.InvalidArgument($"stlDeviationMm and stlAngleDeg only apply to stlQuality 'custom' (got '{quality}').",
                    "Pass stlQuality:'custom' with them, or drop them.");
            if (q.StlDeviationMm is double dev && (!double.IsFinite(dev) || dev <= 0))
                throw SwException.InvalidArgument($"stlDeviationMm must be a positive length in mm (got {dev}).");
            if (q.StlAngleDeg is double ang && (!double.IsFinite(ang) || ang <= 0 || ang >= 90))
                throw SwException.InvalidArgument($"stlAngleDeg must be between 0 and 90 degrees (got {ang}).");
        }
        return new ExportPlan(format, quality, byExt is not null);
    }

    /// <summary>The file to write for a document of SOLIDWORKS type <paramref name="docType"/>: the extension is added when missing.</summary>
    public string FileFor(string path, int docType)
    {
        string p = path.Trim().Trim('"');
        if (Format.Name != "edrawings") return PathHasFormatExtension ? p : p + Format.Extensions[0];
        string want = docType switch
        {
            (int)swDocumentTypes_e.swDocASSEMBLY => ".easm",
            (int)swDocumentTypes_e.swDocDRAWING => ".edrw",
            _ => ".eprt",
        };
        if (!PathHasFormatExtension) return p + want;
        string ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
        if (ext != want)
        {
            string type = SwContext.TypeName(docType);
            throw SwException.InvalidArgument($"eDrawings files of {(type == "assembly" ? "an" : "a")} {type} end in {want}, not {ext}.",
                $"Use {System.IO.Path.ChangeExtension(System.IO.Path.GetFileName(p), want)}.");
        }
        return p;
    }
}

/// <summary>
/// Checks that an exported file is what its format promises (pure file I/O, no COM): the signature or header, and
/// format details such as the STEP schema, the STL size formula and mesh volume, image sizes and DWG/DXF releases.
/// </summary>
internal static class ExportFileCheck
{
    /// <summary>What the caller asked for; mismatches become problems.</summary>
    internal sealed record Expect(int? StepAp = null, bool? StlBinary = null, double[]? ModelSizeMm = null,
        bool SingleBody = false, double? BodyVolumeMm3 = null);

    /// <summary>Files up to this size are scanned completely (entity and triangle counts, mesh volume).</summary>
    private const long ScanLimit = 256L * 1024 * 1024;

    private static readonly Regex StepSchemaRx = new(@"FILE_SCHEMA\s*\(\s*\(\s*'([^']*)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StepSolidRx = new(@"=\s*(MANIFOLD_SOLID_BREP|BREP_WITH_VOIDS)\s*\(", RegexOptions.CultureInvariant);
    private static readonly Regex StepShellRx = new(@"=\s*SHELL_BASED_SURFACE_MODEL\s*\(", RegexOptions.CultureInvariant);
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Unit candidates for STL coordinates: name and file units per mm.</summary>
    private static readonly (string Name, double PerMm)[] UnitScales =
        [("mm", 1), ("cm", 0.1), ("m", 0.001), ("in", 1 / 25.4), ("ft", 1 / 304.8), ("um", 1000)];

    /// <summary>Inspects <paramref name="file"/> as <paramref name="format"/>; failed checks are added to <paramref name="problems"/>.</summary>
    public static ExportDetails Inspect(string format, string file, Expect expect, List<string> problems)
    {
        var d = new ExportDetails();
        var checks = new List<string>();
        switch (format)
        {
            case "step": Step(file, expect, d, problems, checks); break;
            case "iges": Iges(file, d, problems, checks); break;
            case "parasolid": Parasolid(file, d, problems, checks); break;
            case "stl": Stl(file, expect, d, problems, checks); break;
            case "3mf": ThreeMf(file, d, problems, checks); break;
            case "png": Png(file, d, problems, checks); break;
            case "jpg": Jpeg(file, d, problems, checks); break;
            case "pdf": Pdf(file, d, problems, checks); break;
            case "dwg": Dwg(file, d, problems, checks); break;
            case "dxf": Dxf(file, d, problems, checks); break;
            default: checks.Add("no known signature for this format: only size and freshness checked"); break;
        }
        d.Checks = checks;
        return d;
    }

    // ------------------------------------------------------------------ STEP

    private static void Step(string file, Expect e, ExportDetails d, List<string> problems, List<string> checks)
    {
        string head = Latin1(Head(file, 64 * 1024));
        string start = head.TrimStart('ï', '»', '¿', '﻿', ' ', '\t', '\r', '\n');
        if (!start.StartsWith("ISO-10303-21;", StringComparison.Ordinal))
        {
            problems.Add("not a STEP file: it does not start with 'ISO-10303-21;'");
            return;
        }
        d.Header = "ISO-10303-21";
        checks.Add("ISO-10303-21 header");
        var m = StepSchemaRx.Match(head);
        if (!m.Success)
        {
            problems.Add("the STEP header has no FILE_SCHEMA");
            return;
        }
        d.Schema = m.Groups[1].Value.Trim();
        d.StepAp = SchemaAp(d.Schema);
        if (e.StepAp is int want)
        {
            if (d.StepAp != want) problems.Add($"FILE_SCHEMA is '{d.Schema}'{(d.StepAp is int got ? $" (AP{got})" : "")}, not AP{want}");
            else checks.Add($"FILE_SCHEMA {d.Schema} = AP{want}");
        }
        if (new FileInfo(file).Length > ScanLimit)
        {
            if (e.SingleBody) problems.Add("the STEP is too large to verify its selected-body scope");
            return;
        }
        int solids = 0, shells = 0;
        foreach (var line in Lines(file))
        {
            if (line.Contains("SOLID_BREP", StringComparison.Ordinal) || line.Contains("BREP_WITH_VOIDS", StringComparison.Ordinal))
                solids += StepSolidRx.Count(line);
            if (line.Contains("SHELL_BASED_SURFACE_MODEL", StringComparison.Ordinal)) shells += StepShellRx.Count(line);
        }
        d.Solids = solids;
        if (shells > 0) d.SurfaceModels = shells;
        if (e.SingleBody)
        {
            if (solids != 1 || shells != 0)
                problems.Add($"selected-body STEP must contain one B-rep solid and no surface models (found {solids} solids, {shells} surface models)");
            else checks.Add("one B-rep solid and no surface models");
            if (!Latin1(Tail(file, 2048)).TrimEnd().EndsWith("END-ISO-10303-21;", StringComparison.Ordinal))
                problems.Add("the STEP has no END-ISO-10303-21 trailer");
        }
    }

    /// <summary>The application protocol a STEP FILE_SCHEMA belongs to (null when unknown).</summary>
    internal static int? SchemaAp(string schema)
    {
        string s = schema.ToUpperInvariant();
        if (s.Contains("AUTOMOTIVE_DESIGN", StringComparison.Ordinal) || s.StartsWith("AP214", StringComparison.Ordinal)) return 214;
        if (s.Contains("CONFIG_CONTROL_DESIGN", StringComparison.Ordinal) || s.StartsWith("AP203", StringComparison.Ordinal)) return 203;
        if (s.StartsWith("AP242", StringComparison.Ordinal) || s.Contains("AP242_", StringComparison.Ordinal)) return 242;
        return null;
    }

    // ------------------------------------------------------------------ IGES / Parasolid

    private static void Iges(string file, ExportDetails d, List<string> problems, List<string> checks)
    {
        string first = Latin1(Head(file, 4096)).Split('\n')[0].TrimEnd('\r');
        if (first.Length < 73 || first[72] != 'S')
        {
            problems.Add("not an IGES file: line 1 has no 'S' (start section) in column 73");
            return;
        }
        d.Header = "IGES";
        checks.Add("IGES start section (S in column 73)");
        string last = Latin1(Tail(file, 2048)).Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.Trim().Length > 0) ?? "";
        if (last.Length < 73 || last[72] != 'T')
        {
            problems.Add("the IGES file has no terminate (T) section at the end: it may be truncated");
            return;
        }
        checks.Add("IGES terminate section");
        var dm = Regex.Match(last[..72], @"D\s*(\d+)");
        if (dm.Success && int.TryParse(dm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dLines))
            d.Entities = dLines / 2; // two directory-entry lines per entity
    }

    private static void Parasolid(string file, ExportDetails d, List<string> problems, List<string> checks)
    {
        string head = Latin1(Head(file, 4096));
        if (!head.StartsWith("**", StringComparison.Ordinal) || !head.Contains("PARASOLID", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("not a Parasolid text (x_t) file: no '**…PARASOLID' transmit header");
            return;
        }
        d.Header = "Parasolid transmit (text)";
        checks.Add("Parasolid transmit header");
        var sch = Regex.Match(head, @"SCH=([A-Za-z0-9_]+)");
        if (sch.Success) d.Version = sch.Groups[1].Value;
    }

    // ------------------------------------------------------------------ STL

    private static void Stl(string file, Expect e, ExportDetails d, List<string> problems, List<string> checks)
    {
        long length = new FileInfo(file).Length;
        var head = Head(file, 84);
        bool binary = false;
        uint count = 0;
        if (length >= 84)
        {
            count = BitConverter.ToUInt32(head, 80);
            binary = length == 84 + 50L * count;
        }
        Mesh mesh;
        if (binary)
        {
            checks.Add($"binary STL: {length} bytes = 84 + 50 x {count} triangles");
            mesh = length <= ScanLimit ? BinaryMesh(file, count) : new Mesh { Triangles = count };
        }
        else
        {
            if (!Latin1(head).TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"not an STL file: {length} bytes is not 84 + 50 x n (binary) and it does not start with 'solid' (ASCII)");
                return;
            }
            mesh = e.SingleBody && length <= ScanLimit
                ? CheckedAsciiMesh(file, problems)
                : AsciiMesh(file, length <= ScanLimit);
            checks.Add($"ASCII STL: {mesh.Triangles} facets");
        }
        d.Header = binary ? "STL (binary)" : "STL (ASCII)";
        d.Binary = binary;
        d.Triangles = mesh.Triangles;
        if (mesh.Triangles == 0) problems.Add("the STL contains no triangles");
        if (e.StlBinary is bool want && want != binary)
            problems.Add($"the STL is {(binary ? "binary" : "ASCII")}, but {(want ? "binary" : "ASCII")} was requested");
        if (!mesh.HasGeometry)
        {
            if (e.SingleBody) problems.Add("the selected-body STL could not be scanned for dimensions and volume");
            return;
        }

        var size = mesh.Size;
        double volume = Math.Abs(mesh.Volume);
        if (e.SingleBody)
        {
            // The selected-body path explicitly sets millimetres. Unit inference could conceal a wrong-scale file.
            if (e.ModelSizeMm is not { Length: 3 } expected ||
                !size.Zip(expected).All(p => double.IsFinite(p.First) && Math.Abs(p.First - p.Second) <= Math.Max(0.02, 0.01 * p.Second)))
                problems.Add("the STL's three dimensions in mm do not match the selected body");
            else checks.Add("all three STL dimensions match the selected body (mm)");
            if (e.BodyVolumeMm3 is not double wantVolume || !double.IsFinite(volume) || volume <= 0 ||
                Math.Abs(volume - wantVolume) > 0.05 * wantVolume)
                problems.Add("the STL mesh volume in mm³ differs from the selected body by more than 5%");
            else checks.Add("STL mesh volume matches the selected body within 5%");
            d.Units = "mm";
            d.SizeMm = size.Select(v => Math.Round(v, 4)).ToArray();
            d.VolumeMm3 = Math.Round(volume, 3);
            return;
        }
        var (unit, perMm) = InferUnits(size, e.ModelSizeMm);
        if (unit is not null)
        {
            d.Units = unit;
            d.SizeMm = size.Select(v => Math.Round(v / perMm, 4)).ToArray();
            d.VolumeMm3 = Math.Round(volume / (perMm * perMm * perMm), 3);
            checks.Add($"mesh extents match the model's (coordinates in {unit})");
        }
        else
        {
            d.MeshSize = size.Select(v => Math.Round(v, 6)).ToArray();
            d.MeshVolume = Math.Round(volume, 6);
            if (e.ModelSizeMm is not null) checks.Add("mesh extents don't match the model's in mm, cm, m, in or ft (reported in file units)");
        }
    }

    /// <summary>The unit whose scale maps the mesh's largest extent onto the model's (1% tolerance), else null.</summary>
    internal static (string? Unit, double PerMm) InferUnits(double[] meshSize, double[]? modelSizeMm)
    {
        if (modelSizeMm is not { Length: 3 } || meshSize.Length != 3) return (null, 1);
        double model = modelSizeMm.Max(), mesh = meshSize.Max();
        if (model < 1e-6 || mesh <= 0) return (null, 1);
        double ratio = mesh / model;
        foreach (var (name, perMm) in UnitScales)
            if (Math.Abs(ratio / perMm - 1) < 0.01) return (name, perMm);
        return (null, 1);
    }

    private sealed class Mesh
    {
        public long Triangles;
        public double Volume; // signed, file units³
        private double _x0 = double.MaxValue, _y0 = double.MaxValue, _z0 = double.MaxValue;
        private double _x1 = double.MinValue, _y1 = double.MinValue, _z1 = double.MinValue;

        public bool HasGeometry => Triangles > 0 && _x0 <= _x1;
        public double[] Size => [_x1 - _x0, _y1 - _y0, _z1 - _z0];

        public void Add(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
        {
            Triangles++;
            // Divergence theorem: the signed tetrahedra from the origin sum to the enclosed volume of a closed mesh.
            Volume += (ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx)) / 6.0;
            Extend(ax, ay, az);
            Extend(bx, by, bz);
            Extend(cx, cy, cz);
        }

        private void Extend(double x, double y, double z)
        {
            _x0 = Math.Min(_x0, x); _y0 = Math.Min(_y0, y); _z0 = Math.Min(_z0, z);
            _x1 = Math.Max(_x1, x); _y1 = Math.Max(_y1, y); _z1 = Math.Max(_z1, z);
        }
    }

    private static Mesh BinaryMesh(string file, uint count)
    {
        var mesh = new Mesh();
        using var fs = OpenShared(file);
        fs.Position = 84;
        const int Batch = 4096;
        var buf = new byte[50 * Batch];
        long left = count;
        while (left > 0)
        {
            int n = (int)Math.Min(left, Batch);
            fs.ReadExactly(buf, 0, n * 50);
            for (int i = 0; i < n; i++)
            {
                int o = i * 50 + 12; // skip the facet normal
                mesh.Add(F(buf, o), F(buf, o + 4), F(buf, o + 8), F(buf, o + 12), F(buf, o + 16), F(buf, o + 20),
                    F(buf, o + 24), F(buf, o + 28), F(buf, o + 32));
            }
            left -= n;
        }
        return mesh;

        static double F(byte[] b, int o) => BitConverter.ToSingle(b, o);
    }

    private static Mesh AsciiMesh(string file, bool geometry)
    {
        var mesh = new Mesh();
        var v = new double[9];
        int k = 0;
        long facets = 0;
        foreach (var raw in Lines(file))
        {
            var line = raw.AsSpan().Trim();
            if (line.StartsWith("facet", StringComparison.OrdinalIgnoreCase))
            {
                facets++;
                k = 0;
            }
            else if (geometry && k < 9 && line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line[6..].ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                for (int j = 0; j < 3; j++)
                    v[k + j] = double.TryParse(parts[j], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : double.NaN;
                k += 3;
                if (k == 9 && v.All(double.IsFinite)) mesh.Add(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);
            }
        }
        if (!geometry || mesh.Triangles < facets) mesh.Triangles = facets;
        return mesh;
    }

    /// <summary>Reads complete facets and the end marker before accepting a selected body's mesh measurements.</summary>
    private static Mesh CheckedAsciiMesh(string file, List<string> problems)
    {
        var mesh = new Mesh();
        using var lines = Lines(file).Select(l => l.Trim()).Where(l => l.Length != 0).GetEnumerator();
        var vertices = new double[9];
        if (!ReadWord("solid")) return Incomplete();
        while (lines.MoveNext())
        {
            if (StartsWord(lines.Current, "endsolid")) return lines.MoveNext() ? Incomplete() : mesh;
            if (!StartsWord(lines.Current, "facet") || !StartsWord(lines.Current.AsSpan(5).TrimStart(), "normal") ||
                !ReadWord("outer") || !lines.Current.AsSpan(5).Trim().Equals("loop", StringComparison.OrdinalIgnoreCase))
                return Incomplete();
            for (int vertex = 0; vertex < 3; vertex++)
            {
                if (!ReadWord("vertex")) return Incomplete();
                var parts = lines.Current[6..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) return Incomplete();
                for (int axis = 0; axis < 3; axis++)
                {
                    if (!double.TryParse(parts[axis], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                        !double.IsFinite(value)) return Incomplete();
                    vertices[vertex * 3 + axis] = value;
                }
            }
            if (!ReadLine("endloop") || !ReadLine("endfacet")) return Incomplete();
            mesh.Add(vertices[0], vertices[1], vertices[2], vertices[3], vertices[4], vertices[5], vertices[6], vertices[7], vertices[8]);
        }
        return Incomplete();

        bool ReadWord(string word) => lines.MoveNext() && StartsWord(lines.Current, word);
        bool ReadLine(string line) => lines.MoveNext() && lines.Current.Equals(line, StringComparison.OrdinalIgnoreCase);
        Mesh Incomplete()
        {
            problems.Add("the selected-body ASCII STL has an incomplete facet or missing endsolid marker");
            return mesh;
        }

        static bool StartsWord(ReadOnlySpan<char> line, string word) => line.StartsWith(word, StringComparison.OrdinalIgnoreCase) &&
            (line.Length == word.Length || char.IsWhiteSpace(line[word.Length]));
    }

    // ------------------------------------------------------------------ 3MF

    private static void ThreeMf(string file, ExportDetails d, List<string> problems, List<string> checks)
    {
        var head = Head(file, 4);
        if (head.Length < 4 || head[0] != (byte)'P' || head[1] != (byte)'K' || head[2] != 3 || head[3] != 4)
        {
            problems.Add("not a 3MF file: it is not a ZIP package (no 'PK' header)");
            return;
        }
        d.Header = "3MF (ZIP)";
        try
        {
            using var zip = new ZipArchive(OpenShared(file), ZipArchiveMode.Read);
            var model = zip.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/').TrimStart('/').Equals("3D/3dmodel.model", StringComparison.OrdinalIgnoreCase))
                        ?? zip.Entries.FirstOrDefault(x => x.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
            if (model is null)
            {
                problems.Add("the 3MF package has no 3D model part (3D/3dmodel.model)");
                return;
            }
            checks.Add($"3MF package with {model.FullName}");
            if (model.Length > ScanLimit) return;
            using var reader = new StreamReader(model.Open(), Encoding.UTF8);
            string xml = reader.ReadToEnd();
            long triangles = Occurrences(xml, "<triangle ");
            d.Triangles = triangles;
            var unit = Regex.Match(xml.Length > 8192 ? xml[..8192] : xml, @"<model\b[^>]*\bunit\s*=\s*""([A-Za-z]+)""");
            d.Units = unit.Success ? unit.Groups[1].Value : "millimeter"; // the 3MF default
            if (triangles == 0) problems.Add("the 3MF model contains no triangles");
        }
        catch (InvalidDataException ex)
        {
            problems.Add($"the 3MF ZIP package is damaged ({ex.Message})");
        }
    }

    private static long Occurrences(string text, string token)
    {
        long n = 0;
        for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    // ------------------------------------------------------------------ images, PDF, DWG, DXF

    private static void Png(string file, ExportDetails d, List<string> problems, List<string> checks)
    {
        var h = Head(file, 32);
        if (h.Length < 24 || !h.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            problems.Add("not a PNG file (no PNG signature)");
            return;
        }
        d.Header = "PNG";
        checks.Add("PNG signature");
        if (Encoding.ASCII.GetString(h, 12, 4) == "IHDR")
        {
            d.WidthPx = BigEndian32(h, 16);
            d.HeightPx = BigEndian32(h, 20);
        }
        if (d.WidthPx is not > 0 || d.HeightPx is not > 0) problems.Add("the PNG has no image header (IHDR) with a size");
        else checks.Add($"{d.WidthPx} x {d.HeightPx} px");
    }

    private static void Jpeg(string file, ExportDetails d, List<string> problems, List<string> checks)
    {
        var h = Head(file, 1 << 20);
        if (h.Length < 4 || h[0] != 0xFF || h[1] != 0xD8 || h[2] != 0xFF)
        {
            problems.Add("not a JPEG file (no FF D8 FF start of image)");
            return;
        }
        d.Header = "JPEG";
        checks.Add("JPEG start of image");
        int i = 2;
        while (i + 9 < h.Length)
        {
            if (h[i] != 0xFF) { i++; continue; }
            byte marker = h[i + 1];
            if (marker == 0xFF) { i++; continue; }                                        // fill byte
            if (marker is 0x01 or 0xD8 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; } // markers without a length
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))      // start of frame
            {
                d.HeightPx = BigEndian16(h, i + 5);
                d.WidthPx = BigEndian16(h, i + 7);
                break;
            }
            if (marker == 0xDA) break; // start of scan before any frame header
            i += 2 + BigEndian16(h, i + 2);
        }
        if (d.WidthPx is not > 0 || d.HeightPx is not > 0) problems.Add("the JPEG has no frame header with an image size");
        else checks.Add($"{d.WidthPx} x {d.HeightPx} px");
    }

    private static void Pdf(string file, ExportDetails d, List<string> problems, List<string> checks)
    {
        string head = Latin1(Head(file, 16));
        if (!head.StartsWith("%PDF-", StringComparison.Ordinal))
        {
            problems.Add("not a PDF file (no %PDF- header)");
            return;
        }
        d.Header = "PDF";
        d.Version = new string(head[5..].TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        checks.Add($"%PDF-{d.Version} header");
        if (Latin1(Tail(file, 2048)).Contains("%%EOF", StringComparison.Ordinal)) checks.Add("%%EOF trailer");
        else problems.Add("the PDF has no %%EOF trailer: it may be truncated");
    }

    private static void Dwg(string file, ExportDetails d, List<string> problems, List<string> checks)
    {
        string head = Latin1(Head(file, 6));
        if (head.Length < 6 || !head.StartsWith("AC10", StringComparison.Ordinal) || !head[4..].All(char.IsAsciiDigit))
        {
            problems.Add("not a DWG file (no ACxxxx version header)");
            return;
        }
        d.Header = "DWG";
        d.Version = Release(head);
        checks.Add($"DWG {d.Version}");
    }

    private static void Dxf(string file, ExportDetails d, List<string> problems, List<string> checks)
    {
        string head = Latin1(Head(file, 256 * 1024));
        if (head.StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
        {
            d.Header = "DXF (binary)";
            checks.Add("binary DXF sentinel");
            return;
        }
        var first = head.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(2).ToArray();
        if (first.Length < 2 || first[0] != "0" || !first[1].Equals("SECTION", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("not a DXF file (it does not start with group 0 / SECTION)");
            return;
        }
        d.Header = "DXF";
        checks.Add("DXF 0/SECTION start");
        var v = Regex.Match(head, @"\$ACADVER\s*\r?\n\s*1\s*\r?\n\s*(AC\d{4})");
        if (v.Success) d.Version = Release(v.Groups[1].Value);
    }

    private static string Release(string acVersion) => acVersion switch
    {
        "AC1012" => "AC1012 (R13)",
        "AC1014" => "AC1014 (R14)",
        "AC1015" => "AC1015 (2000)",
        "AC1018" => "AC1018 (2004)",
        "AC1021" => "AC1021 (2007)",
        "AC1024" => "AC1024 (2010)",
        "AC1027" => "AC1027 (2013)",
        "AC1032" => "AC1032 (2018)",
        _ => acVersion,
    };

    // ------------------------------------------------------------------ helpers

    /// <summary>Opens for reading while tolerating a writer that hasn't released the file yet.</summary>
    internal static FileStream OpenShared(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static IEnumerable<string> Lines(string file)
    {
        using var reader = new StreamReader(OpenShared(file), Encoding.Latin1);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static byte[] Head(string file, int max)
    {
        using var fs = OpenShared(file);
        var buf = new byte[(int)Math.Min(max, fs.Length)];
        fs.ReadExactly(buf);
        return buf;
    }

    private static byte[] Tail(string file, int max)
    {
        using var fs = OpenShared(file);
        int n = (int)Math.Min(max, fs.Length);
        fs.Seek(-n, SeekOrigin.End);
        var buf = new byte[n];
        fs.ReadExactly(buf);
        return buf;
    }

    private static string Latin1(byte[] b) => Encoding.Latin1.GetString(b);

    private static int BigEndian32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    private static int BigEndian16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
}

/// <summary>
/// Read-only check that a call left SOLIDWORKS' allow-listed system options as it found them. A PreferenceScope restores
/// what it wrote, but SOLIDWORKS may change coupled options as a side effect (STL deviation/angle when the STL
/// resolution switches); those are invisible to the scope, so they are reported instead of silently kept.
/// </summary>
internal static class PrefAudit
{
    public static Dictionary<(PrefKind Kind, int Pref), string> Take(ISldWorks app)
    {
        var values = new Dictionary<(PrefKind, int), string>();
        foreach (var id in SystemPrefIds.All)
        {
            try { values[id] = PreferenceScope.Read(app, id.Kind, id.Pref); }
            catch (COMException) { /* not readable in this release: not audited */ }
        }
        return values;
    }

    /// <summary>Warns (result and log) about options that differ from <paramref name="before"/> after the scope was restored.</summary>
    public static void Check(SwContext ctx, Dictionary<(PrefKind Kind, int Pref), string> before)
    {
        var journaled = ctx.Session.Journal.Entries.Select(e => (e.Kind, e.Pref)).ToHashSet(); // already reported by the scope
        var drift = new List<string>();
        foreach (var ((kind, pref), old) in before)
        {
            if (journaled.Contains((kind, pref))) continue;
            string now;
            try { now = PreferenceScope.Read(ctx.App, kind, pref); }
            catch (COMException) { continue; }
            if (!PreferenceScope.Same(kind, now, old)) drift.Add($"{kind} {pref} was {old}, now {now}");
        }
        if (drift.Count == 0) return;
        string message = $"SOLIDWORKS system options changed during this call and are not back to the user's values: {string.Join("; ", drift)}. " +
                         "Tell the user (Tools > Options > System Options).";
        ctx.Warn(message);
        ctx.Session.Log.LogWarning("{Operation}: {Message}", ctx.Op.Name, message);
    }
}

/// <summary>
/// model_export: writes the target document through <c>IModelDocExtension.SaveAs3</c>, whose file extension selects the
/// translator. The format's system options (STEP AP, STL binary/quality, …) are pinned in a PreferenceScope and
/// restored afterwards; the result is verified from the file itself (new, non-empty, header and format details).
/// </summary>
public sealed class ExportService(SwSession session)
{
    public Task<ExportResult> ExportAsync(ExportRequest q, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(q);
        var plan = ExportPlan.Parse(q); // argument errors before anything is queued
        var timeout = TimeSpan.FromSeconds(Math.Max(180, session.Options.DefaultTimeout.TotalSeconds));
        return session.RunAsync("model_export", ctx => Export(ctx, q, plan), RunOptions.Read(q.Doc) with { Timeout = timeout }, ct);
    }

    private static ExportResult Export(SwContext ctx, ExportRequest q, ExportPlan plan) => ExportDoc(ctx, ctx.Doc, q, plan);

    /// <summary>model_export of one document (the bound one, or one model_export_all walks to).</summary>
    internal static ExportResult ExportDoc(SwContext ctx, IModelDoc2 d, ExportRequest q, ExportPlan plan)
    {
        int type = d.GetType();
        var kind = type switch
        {
            (int)swDocumentTypes_e.swDocPART => ExportDocs.Part,
            (int)swDocumentTypes_e.swDocASSEMBLY => ExportDocs.Assembly,
            (int)swDocumentTypes_e.swDocDRAWING => ExportDocs.Drawing,
            _ => (ExportDocs)0,
        };
        var f = plan.Format;
        if ((f.Docs & kind) == 0)
            throw new SwException(ErrorCodes.WrongDocType,
                $"{f.Label} export needs a {ExportFormats.Describe(f.Docs)}, but '{ctx.Title(d)}' is a {SwContext.TypeName(type)}.",
                f.Docs == ExportDocs.Drawing
                    ? "Make a drawing of the model first, or export the model as step, stl, pdf or png."
                    : "Export the part or assembly itself (doc_activate it, or pass 'doc').");
        string file = plan.FileFor(q.Path, type);
        bool image = f.Name is "png" or "jpg";
        if (kind != ExportDocs.Drawing && !image && d.SketchManager?.ActiveSketch is ISketch open)
        {
            string sketch = ((IFeature)open).Name;
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{sketch}' is open in '{ctx.Title(d)}'; export the model with no sketch in edit.",
                ctx.Documents.Find(d)?.ServerSketch == sketch ? "Finish it with sketch_finish first." : "The user is editing it; ask them to exit the sketch.");
        }
        ctx.CheckUserCommand();
        var body = q.Body is null ? null : ExportBody(ctx, d, kind, f, q.Body);
        double[]? modelMm = body is null ? ModelSizeMm(ctx, d, kind, f) : body.Shape?.SizeMm;
        using var stage = body is null ? null : new SelectedBodyExportStage(ctx.Paths, file, q.Overwrite);
        string? backup = null;
        string full = stage?.Destination ?? ctx.Paths.ForWrite(file, q.Overwrite, out backup);
        string translatorPath = stage?.TranslatorPath ?? full;
        WarnIgnoredOptions(ctx, q, f);

        var dir = System.IO.Path.GetDirectoryName(full)!;
        var before = stage is null ? DirectoryNames(dir) : null;
        string? previous = stage is null ? ActualFile(full) : null;
        string? aside = previous is null ? null : MoveAside(previous);
        string? written = null;
        bool keep = false;
        var settings = new List<string>();
        var start = DateTime.UtcNow;
        try
        {
            int errors = 0, warnings = 0;
            bool ok;
            var options = PrefAudit.Take(ctx.App);
            try
            {
                using var prefs = ctx.SystemPrefs();
                if (f.Name == "step") SetStepAp(ctx, prefs, q.StepAp, settings);
                else if (f.Name == "stl")
                {
                    SetStl(ctx, prefs, q, plan.Quality, settings);
                    if (body is not null)
                    {
                        prefs.Integer(SystemPrefIds.StlUnits, (int)swLengthUnit_e.swMM);
                        if (ctx.App.GetUserPreferenceIntegerValue(SystemPrefIds.StlUnits) != (int)swLengthUnit_e.swMM)
                            throw new SwException(ErrorCodes.VerifyFailed, "SOLIDWORKS did not accept millimetres for the selected-body STL.");
                        settings.Add("STL units mm");
                    }
                }
                // SW2024 accepted a body selection but wrote an empty STL. Export a verified
                // one-body scratch part as a whole model for this translator.
                ok = body is not null && f.Name == "stl"
                    ? SaveIsolatedStl(ctx, translatorPath, f, body, ref errors, ref warnings)
                    : Save(ctx, d, translatorPath, f, kind, body?.Reference, ref errors, ref warnings);
            }
            finally
            {
                PrefAudit.Check(ctx, options); // after the scope restored its own changes
            }
            written = ok ? WaitForFile(translatorPath) : ActualFile(translatorPath);
            // Some SOLIDWORKS 2024 installs write the PDF completely but still
            // return swGenericSaveError. Do not discard a potentially valid PDF:
            // the freshness, size, PDF signature and EOF checks below still gate it.
            bool verifyGenericPdf = !ok && f.Name == "pdf" &&
                errors == (int)swFileSaveError_e.swGenericSaveError && written is not null;
            if (!ok && !verifyGenericPdf)
                throw new SwException(ErrorCodes.IoError, $"SOLIDWORKS could not export '{full}' ({DocumentService.SaveErrors(errors)}).", SaveHint(errors, f));
            if (written is null)
                throw new SwException(ErrorCodes.VerifyFailed, $"SOLIDWORKS reported success but did not write '{System.IO.Path.GetFileName(full)}'.{(before is null ? "" : Strays(dir, before, start))}",
                    "Check dialog_list for a message; for assemblies, components may have been written as separate files.");

            var fi = new FileInfo(written);
            var problems = new List<string>();
            if (fi.Length == 0) problems.Add("the file is empty");
            if (fi.LastWriteTimeUtc < start.AddSeconds(-2)) problems.Add($"its time stamp ({fi.LastWriteTimeUtc:u}) is older than this call");
            var details = problems.Count > 0
                ? new ExportDetails()
                : ExportFileCheck.Inspect(f.Name, written, new ExportFileCheck.Expect(
                    f.Name == "step" ? q.StepAp : null, f.Name == "stl" ? q.StlBinary : null, modelMm,
                    SingleBody: body is not null, BodyVolumeMm3: body?.Shape?.VolumeMm3), problems);
            if (problems.Count > 0)
            {
                string removed = stage is not null ? "The staged file was discarded."
                    : $"It was removed{(aside is null ? "" : " and the previous file put back")}.";
                // Live: with option 75 reading 242, SOLIDWORKS 2025 Premium still wrote AP203 (AP242 output can depend on
                // the license, e.g. SOLIDWORKS MBD). That is a capability limit, not a broken file.
                if (f.Name == "step" && q.StepAp == 242 && details.StepAp is int got && got != 242)
                    throw new SwException(ErrorCodes.NotSupported,
                        $"This SOLIDWORKS wrote STEP AP{got} although AP242 was selected (AP242 output can need an extra license, " +
                        $"such as SOLIDWORKS MBD). {removed}",
                        "Use stepAp 214 (AUTOMOTIVE_DESIGN, read by most CAD/CAM software) or 203.");
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"The exported '{System.IO.Path.GetFileName(stage is null ? written : full)}' failed verification: {string.Join("; ", problems)}. {removed}",
                    f.Name == "stl" ? "Retry with other stl* options, or check the model (model_info)." : "Check the model (model_info), or try another format.");
            }
            if (verifyGenericPdf)
                ctx.Warn("SOLIDWORKS returned swGenericSaveError although it wrote a fresh PDF with a valid signature and EOF. " +
                         "The PDF passed file verification; review its rendered content before using it.");
            if (warnings != 0) ctx.Warn($"Export warnings: {DocumentService.SaveWarnings(warnings)}.");
            long size = fi.Length;
            bool replaced = previous is not null;
            if (stage is not null)
                (written, replaced, backup) = stage.Publish(written);
            details.Checks = [$"new file written by this call ({size} bytes)", .. details.Checks ?? []];
            ctx.Paths.RecordWritten(written);
            keep = true;
            return new ExportResult
            {
                Doc = ctx.Title(d),
                Path = written,
                Format = f.Name,
                SizeBytes = size,
                Settings = settings.Count == 0 ? null : settings,
                Details = details,
                Resolved = body is null ? null : [body.Reference.Info("body")],
                Replaced = replaced ? true : null,
                BackupPath = backup,
            };
        }
        finally
        {
            // The stage's own cleanup never looks up or deletes the final destination.
            if (stage is null)
            {
                if (keep)
                {
                    if (aside is not null) TryDelete(aside);
                }
                else
                {
                    // Roll back: nothing this call wrote stays, and the file it replaced comes back.
                    var mine = written ?? ActualFile(full);
                    if (mine is not null) TryDelete(mine);
                    if (aside is not null && previous is not null && !TryMove(aside, previous))
                        ctx.Warn($"The previous file could not be put back; it is at '{aside}'.");
                }
            }
        }
    }

    /// <summary>A same-directory staging file for a selected-body export. Publish only after file verification.</summary>
    internal sealed class SelectedBodyExportStage : IDisposable
    {
        private readonly PathPolicy _paths;
        private readonly bool _overwrite;
        private bool _published;

        public SelectedBodyExportStage(PathPolicy paths, string path, bool overwrite)
        {
            _paths = paths;
            _overwrite = overwrite;
            Destination = paths.CheckWritePath(path, overwrite);
            TranslatorPath = Path.Combine(Path.GetDirectoryName(Destination)!,
                $"swmcp-export-{Guid.NewGuid():N}{Path.GetExtension(Destination)}");
        }

        public string Destination { get; }
        public string TranslatorPath { get; }

        public (string Path, bool Replaced, string? BackupPath) Publish(string verifiedFile)
        {
            if (_published || !string.Equals(verifiedFile, TranslatorPath, StringComparison.OrdinalIgnoreCase))
                throw new SwException(ErrorCodes.VerifyFailed, "The selected-body export did not return its own staging file.");

            // Use the existing file's exact name on replacement. A new file follows the translator's extension case.
            string? existing = ActualFile(Destination);
            string target = existing ?? Path.ChangeExtension(Destination, Path.GetExtension(verifiedFile));
            bool replace = _overwrite && existing is not null;
            try
            {
                target = _paths.ForWrite(target, replace, out string? backup);
                if (replace)
                    File.Replace(verifiedFile, target, null);
                else
                    File.Move(verifiedFile, target, overwrite: false);
                _published = true;
                return (target, replace, backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SwException(ErrorCodes.IoError, $"Could not publish the verified export to '{target}': {ex.Message}",
                    "The destination may have changed or be locked. Choose another name or retry.", ex);
            }
        }

        public void Dispose()
        {
            if (!_published && ActualFile(TranslatorPath) is string candidate) TryDelete(candidate);
        }
    }

    private sealed record SelectedExportBody(Resolved Reference, ExportBodyShape? Shape = null);

    private sealed record ExportBodyShape(double[] BoundsMm, double VolumeMm3)
    {
        public double[] SizeMm => [BoundsMm[3] - BoundsMm[0], BoundsMm[4] - BoundsMm[1], BoundsMm[5] - BoundsMm[2]];
    }

    private static SelectedExportBody ExportBody(SwContext ctx, IModelDoc2 d, ExportDocs kind, ExportFormat format, EntityRef body)
    {
        if (kind != ExportDocs.Part)
            throw new SwException(ErrorCodes.WrongDocType, "'body' export needs a part document.");
        var reference = BodyKit.ResolveBody(ctx, d, body, "body", BodyKind.Solid);
        var solid = (IBody2)reference.Obj;
        if (!((IPartDoc)d).Bodies(swBodyType_e.swSolidBody, visibleOnly: true).Any(b => SameExportBody(d, b, solid)))
            throw SwException.InvalidArgument($"{reference.Label} is not a visible solid in this part.");
        return new SelectedExportBody(reference, format.Name == "stl" ? MeasureExportBody(solid, reference.Label) : null);
    }

    private static ExportBodyShape MeasureExportBody(IBody2 solid, string label)
    {
        if (!solid.GetExtremePoint(-1, 0, 0, out double minX, out _, out _) ||
            !solid.GetExtremePoint(0, -1, 0, out _, out double minY, out _) ||
            !solid.GetExtremePoint(0, 0, -1, out _, out _, out double minZ) ||
            !solid.GetExtremePoint(1, 0, 0, out double maxX, out _, out _) ||
            !solid.GetExtremePoint(0, 1, 0, out _, out double maxY, out _) ||
            !solid.GetExtremePoint(0, 0, 1, out _, out _, out double maxZ))
            throw new SwException(ErrorCodes.VerifyFailed, $"Could not measure the exact bounds of {label}.");
        double[] bounds = [Units.Mm(minX), Units.Mm(minY), Units.Mm(minZ), Units.Mm(maxX), Units.Mm(maxY), Units.Mm(maxZ)];
        var mass = solid.MassOfBody();
        double volume = mass.Length > 3 ? Units.Mm3(mass[3]) : 0;
        var shape = new ExportBodyShape(bounds, volume);
        if (!double.IsFinite(volume) || volume <= 0 || shape.SizeMm.Any(s => !double.IsFinite(s) || s <= 0))
            throw new SwException(ErrorCodes.VerifyFailed, $"Could not measure positive dimensions and volume of {label}.");
        return shape;
    }

    private static bool SameExportBody(IModelDoc2 doc, IBody2 a, IBody2 b)
    {
        if (ReferenceEquals(a, b)) return true;
        var ext = doc.Extension;
        var pa = ComArgs.Bytes((object?)ext.GetPersistReference3(a));
        var pb = ComArgs.Bytes((object?)ext.GetPersistReference3(b));
        return pa is not null && pb is not null && ext.IsSamePersistentID(pa, pb) == (int)swObjectEquality.swObjectSame;
    }

    /// <summary>Copies the requested solid into an unsaved part, so STL sees a whole model with one body.</summary>
    private static bool SaveIsolatedStl(SwContext ctx, string full, ExportFormat format, SelectedExportBody requested,
        ref int errors, ref int warnings)
    {
        string template = ctx.App.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
        if (string.IsNullOrWhiteSpace(template) || !File.Exists(template) ||
            !string.Equals(Path.GetExtension(template), ".prtdot", StringComparison.OrdinalIgnoreCase))
            throw new SwException(ErrorCodes.IoError, "Selected-body STL needs a valid default part template.",
                "Set a .prtdot under SOLIDWORKS Tools > Options > Default Templates.");
        if ((object?)((IBody2)requested.Reference.Obj).Copy() is not IBody2 copy || !copy.IsTemporaryBody())
            throw new SwException(ErrorCodes.FeatureFailed, $"Could not copy {requested.Reference.Label} into a temporary body.");
        var previous = ctx.ActiveDocOrNull;
        var openBefore = ctx.App.Documents();
        var scratch = (object?)ctx.App.NewDocument(template, 0, 0, 0) as IModelDoc2
                      ?? throw new SwException(ErrorCodes.FeatureFailed, "Could not create a temporary part for selected-body STL.");
        if (openBefore.Any(d => ReferenceEquals(d, scratch)))
            throw new SwException(ErrorCodes.VerifyFailed, "NewDocument returned an existing document during selected-body STL export.");

        try
        {
            ctx.Documents.SetClass(scratch, DocClass.Owned, "temporary selected-body STL");
            scratch.SetTitle2($"MCP_STL_{Guid.NewGuid():N}");
            if (scratch is not IPartDoc part || part.AllBodies(visibleOnly: false).Count != 0)
                throw new SwException(ErrorCodes.VerifyFailed, "The default part template did not create an empty part.");
            if ((object?)part.CreateFeatureFromBody3(copy, false, (int)swCreateFeatureBodyOpts_e.swCreateFeatureBodyCheck) is not IFeature)
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not insert a checked copy of {requested.Reference.Label} into the temporary part.");
            var bodies = part.AllBodies(visibleOnly: false);
            if (bodies.Count != 1 || bodies[0].GetType() != (int)swBodyType_e.swSolidBody ||
                part.Bodies(swBodyType_e.swSolidBody, visibleOnly: true).Count != 1 || bodies[0].FaultList().Count != 0)
                throw new SwException(ErrorCodes.VerifyFailed, "The temporary part does not contain exactly one valid visible solid.");
            var actual = MeasureExportBody(bodies[0], "the temporary solid");
            var expected = requested.Shape!;
            if (actual.BoundsMm.Zip(expected.BoundsMm).Any(p => Math.Abs(p.First - p.Second) > 0.0001) ||
                Math.Abs(actual.VolumeMm3 - expected.VolumeMm3) > Math.Max(0.002, expected.VolumeMm3 * 0.0001))
                throw new SwException(ErrorCodes.VerifyFailed, "The temporary solid does not match the requested body's bounds and volume.");
            return Save(ctx, scratch, full, format, ExportDocs.Part, null, ref errors, ref warnings);
        }
        finally
        {
            try
            {
                // CloseDoc is title-based. Close only the object created by this call.
                string title = ctx.Title(scratch);
                if (string.IsNullOrWhiteSpace(title))
                    throw new SwException(ErrorCodes.VerifyFailed, "The temporary export part has no safe title for cleanup.");
                var matching = ctx.App.Documents().Where(d => string.Equals(ctx.Title(d), title, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matching.Count != 1 || !ReferenceEquals(matching[0], scratch))
                    throw new SwException(ErrorCodes.VerifyFailed, $"Could not uniquely identify temporary part '{title}' for cleanup.");
                ctx.App.CloseDoc(title);
                if (ctx.App.Documents().Any(d => ReferenceEquals(d, scratch)))
                    throw new SwException(ErrorCodes.VerifyFailed, $"Temporary part '{title}' could not be closed.");
                ctx.Documents.Remove(scratch);
                ctx.Entities.Forget(scratch);
            }
            finally
            {
                if (previous is not null && (ctx.ActiveDocOrNull is not IModelDoc2 active || !DocLocator.SameDoc(active, previous)))
                {
                    int restoreError = 0;
                    ctx.App.ActivateDoc3(ctx.Title(previous), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref restoreError);
                    if (ctx.ActiveDocOrNull is not IModelDoc2 restored || !DocLocator.SameDoc(restored, previous))
                        ctx.Warn($"The previous active document could not be restored after STL export (error {restoreError}).");
                }
            }
        }
    }

    /// <summary>The model's extents in mm (for the STL unit check); refuses an export of a model without geometry.</summary>
    private static double[]? ModelSizeMm(SwContext ctx, IModelDoc2 d, ExportDocs kind, ExportFormat f)
    {
        bool model = f.Docs == ExportDocs.Model;
        if (kind == ExportDocs.Part)
        {
            var bodies = ((IPartDoc)d).AllBodies(visibleOnly: true);
            if (model && bodies.Count == 0)
                throw SwException.InvalidArgument($"'{ctx.Title(d)}' has no visible bodies, so there is nothing to export as {f.Label}.",
                    "Model something first (feature_extrude), or show the hidden bodies.");
            double[]? box = null;
            foreach (var b in bodies)
            {
                var bb = b.Box();
                if (bb.Length != 6) continue;
                box = box is null ? bb : [Math.Min(box[0], bb[0]), Math.Min(box[1], bb[1]), Math.Min(box[2], bb[2]),
                    Math.Max(box[3], bb[3]), Math.Max(box[4], bb[4]), Math.Max(box[5], bb[5])];
            }
            return box is null ? null : [Units.Mm(box[3] - box[0]), Units.Mm(box[4] - box[1]), Units.Mm(box[5] - box[2])];
        }
        if (kind == ExportDocs.Assembly)
        {
            var asm = (IAssemblyDoc)d;
            if (model && asm.GetComponentCount(false) == 0)
                throw SwException.InvalidArgument($"Assembly '{ctx.Title(d)}' has no components, so there is nothing to export as {f.Label}.");
            var b = asm.AsmBox();
            return b.Length == 6 ? [Units.Mm(b[3] - b[0]), Units.Mm(b[4] - b[1]), Units.Mm(b[5] - b[2])] : null;
        }
        return null;
    }

    private static void WarnIgnoredOptions(SwContext ctx, ExportRequest q, ExportFormat f)
    {
        if (f.Name != "step" && q.StepAp != 214) ctx.Warn("stepAp only applies to STEP exports; it was ignored.");
        if (f.Name != "stl" && (!q.StlBinary || !string.Equals(q.StlQuality?.Trim(), "fine", StringComparison.OrdinalIgnoreCase) ||
                                q.StlDeviationMm is not null || q.StlAngleDeg is not null))
            ctx.Warn("The stl* options only apply to STL exports; they were ignored.");
    }

    // ------------------------------------------------------------------ system options

    private static void SetStepAp(SwContext ctx, PreferenceScope prefs, int ap, List<string> settings)
    {
        prefs.Integer(SystemPrefIds.StepAp, ap);
        int now = ctx.App.GetUserPreferenceIntegerValue(SystemPrefIds.StepAp);
        if (now != ap)
            throw new SwException(ErrorCodes.VerifyFailed, $"The STEP application protocol could not be set to AP{ap} (it reads {now}); nothing was exported.");
        settings.Add($"STEP AP{ap}");
    }

    private static void SetStl(SwContext ctx, PreferenceScope prefs, ExportRequest q, string quality, List<string> settings)
    {
        prefs.Toggle(SystemPrefIds.StlShowInfoOnSave, false)    // the information box after saving is modal
             .Toggle(SystemPrefIds.StlBinary, q.StlBinary)
             .Toggle(SystemPrefIds.StlComponentsIntoOneFile, true); // one file, not one per component
        const int custom = (int)swSTLQuality_e.swSTLQuality_Custom;
        int want = quality switch
        {
            "coarse" => (int)swSTLQuality_e.swSTLQuality_Coarse,
            "fine" => (int)swSTLQuality_e.swSTLQuality_Fine,
            _ => custom,
        };
        int current = ctx.App.GetUserPreferenceIntegerValue(SystemPrefIds.StlQuality);
        if (want == custom)
        {
            double dev = q.StlDeviationMm is double dm ? Units.M(dm) : ctx.App.GetUserPreferenceDoubleValue(SystemPrefIds.StlDeviation);
            double ang = q.StlAngleDeg is double ad ? Units.Rad(ad) : ctx.App.GetUserPreferenceDoubleValue(SystemPrefIds.StlAngleTolerance);
            // The quality is derived from deviation and angle: non-preset values make it custom by themselves
            // (solidworks-api-notes §10). The scope holds the three as one coupled group and restores them together.
            WriteDouble(ctx, prefs, SystemPrefIds.StlDeviation, dev, force: current != custom);
            WriteDouble(ctx, prefs, SystemPrefIds.StlAngleTolerance, ang, force: current != custom);
        }
        prefs.Integer(SystemPrefIds.StlQuality, want);
        int now = ctx.App.GetUserPreferenceIntegerValue(SystemPrefIds.StlQuality);
        if (now != want)
            throw new SwException(ErrorCodes.VerifyFailed, $"The STL quality could not be set to {quality} (it reads {now}); nothing was exported.",
                quality == "custom" ? "Try other stlDeviationMm / stlAngleDeg values, or stlQuality 'fine'." : null);
        if (ctx.App.GetUserPreferenceToggle(SystemPrefIds.StlBinary) != q.StlBinary)
            throw new SwException(ErrorCodes.VerifyFailed, $"The STL output could not be set to {(q.StlBinary ? "binary" : "ASCII")}; nothing was exported.");
        settings.Add(q.StlBinary ? "STL binary" : "STL ASCII");
        settings.Add(want == custom
            ? $"STL quality custom (deviation {Units.Mm(ctx.App.GetUserPreferenceDoubleValue(SystemPrefIds.StlDeviation))} mm, " +
              $"angle {Math.Round(Units.Deg(ctx.App.GetUserPreferenceDoubleValue(SystemPrefIds.StlAngleTolerance)), 3)} deg)"
            : $"STL quality {quality}");
    }

    /// <summary>Writes a double option; with <paramref name="force"/> a value equal to the current one is still written (via a nudge).</summary>
    private static void WriteDouble(SwContext ctx, PreferenceScope prefs, int id, double value, bool force)
    {
        if (force && SameDouble(ctx.App.GetUserPreferenceDoubleValue(id), value)) prefs.Double(id, Nudged(value));
        prefs.Double(id, value);
    }

    private static double Nudged(double v) => v > 0 ? v * 1.01 : 1e-5;

    private static bool SameDouble(double a, double b) => Math.Abs(a - b) <= 1e-12 * Math.Max(1, Math.Abs(a));

    // ------------------------------------------------------------------ the save itself

    /// <summary>
    /// SaveAs3 with the document active (images and PDFs render its window; the user's active document and selection are
    /// restored afterwards).
    /// </summary>
    private static bool Save(SwContext ctx, IModelDoc2 d, string full, ExportFormat f, ExportDocs kind, Resolved? body,
        ref int errors, ref int warnings)
    {
        bool image = f.Name is "png" or "jpg";
        bool neutral = f.Docs == ExportDocs.Model;
        var previous = ctx.ActiveDocOrNull;
        bool switched = false;
        ISelectionMgr? sel = null;
        bool suspended = false;
        try
        {
            if (previous is null || !DocLocator.SameDoc(previous, d))
            {
                int e = 0;
                switched = true;
                ctx.App.ActivateDoc3(ctx.Title(d), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e);
                var now = ctx.ActiveDocOrNull;
                if (now is null || !DocLocator.SameDoc(now, d))
                {
                    string message = $"'{ctx.Title(d)}' could not be activated for the export (error {e}).";
                    if (neutral) throw new SwException(ErrorCodes.VerifyFailed, message);
                    ctx.Warn(message);
                }
            }
            if (neutral)
            {
                // SaveAs3 otherwise uses ambient face/body selections, even on the whole-model path.
                sel = d.Sel();
                if (body is not null || sel.GetSelectedObjectCount2(-1) != 0)
                {
                    sel.SuspendSelectionList(); // Return value describes the old list, including 0 for an empty list.
                    suspended = true;
                }
                if (sel.GetSelectedObjectCount2(-1) != 0)
                    throw new SwException(ErrorCodes.VerifyFailed, "Could not isolate the selection for the export.");
                if (body is not null)
                {
                    BodyKit.SelectAll(d, [body], 0);
                    if (sel.GetSelectedObjectCount2(-1) != 1 || sel.GetSelectedObjectCount2(0) != 1 ||
                        sel.BdSelected(1) is not IBody2 selected || !SameExportBody(d, selected, (IBody2)body.Obj))
                        throw new SwException(ErrorCodes.VerifyFailed, "The export selection did not contain exactly the requested solid body.");
                }
            }
            else if (image)
            {
                try
                {
                    sel = d.Sel();
                    sel.SuspendSelectionList(); // selected entities would render highlighted
                    suspended = true;
                }
                catch (COMException) { /* nothing selected */ }
                d.GraphicsRedraw2();
            }
            object? data = null;
            if (f.Name == "pdf" && ctx.App.PdfExportData() is IExportPdfData pdf)
            {
                if (pdf.ViewPdfAfterSaving) pdf.ViewPdfAfterSaving = false; // never open a PDF viewer on the user's desktop
                if (kind == ExportDocs.Drawing) pdf.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportAllSheets, ComArgs.Empty);
                data = pdf;
            }
            return d.Extension.SaveAs3(full, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), data, null, ref errors, ref warnings);
        }
        finally
        {
            try
            {
                if (suspended) sel!.ResumeSelectionList2(false);
                if (image) d.GraphicsRedraw2();
            }
            catch (COMException ex)
            {
                ctx.Warn($"The previous selection could not be fully restored after the export: {ex.Message}");
            }
            try
            {
                if (switched && previous is not null)
                {
                    int e = 0;
                    ctx.App.ActivateDoc3(ctx.Title(previous), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e);
                }
            }
            catch (COMException ex)
            {
                ctx.Warn($"The previous active document could not be fully restored after the export: {ex.Message}");
            }
        }
    }

    private static string SaveHint(int errors, ExportFormat f)
    {
        bool Has(swFileSaveError_e e) => (errors & (int)e) != 0;
        if (Has(swFileSaveError_e.swFileLockError)) return "The file is open in another program; close it there or choose another name.";
        if (Has(swFileSaveError_e.swFileSaveFormatNotAvailable) || Has(swFileSaveError_e.swFileSaveAsInvalidFileExtension) ||
            Has(swFileSaveError_e.swFileSaveAsNotSupported))
            return $"This SOLIDWORKS can't write {f.Label} for this document (license, add-in or document type); try another format.";
        if (Has(swFileSaveError_e.swFileSaveAsNameExceedsMaxPathLength)) return "The path is too long; use a shorter folder or file name.";
        if (Has(swFileSaveError_e.swFileSaveWithRebuildError)) return "The model has rebuild errors (feature_list shows them); fix them first.";
        if (Has(swFileSaveError_e.swReadOnlySaveError)) return "The folder or file is read-only; choose another path.";
        return "Check dialog_list for a message, that the folder is writable, and that the model exports in SOLIDWORKS itself.";
    }

    // ------------------------------------------------------------------ files

    /// <summary>The existing file at <paramref name="full"/> with its on-disk name (SOLIDWORKS writes e.g. ".STL"), or null.</summary>
    internal static string? ActualFile(string full)
    {
        string? dir = System.IO.Path.GetDirectoryName(full);
        string name = System.IO.Path.GetFileName(full);
        if (dir is null || !Directory.Exists(dir)) return null;
        try
        {
            foreach (var p in Directory.EnumerateFiles(dir, name))
                if (string.Equals(System.IO.Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase)) return p;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* fall back below */ }
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// The exported file once it exists and has stopped growing (two equal non-zero sizes 50 ms apart), waiting at most
    /// 3 s: a translator may finish writing just after SaveAs returns. Returns what is there at the deadline.
    /// </summary>
    private static string? WaitForFile(string full)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        long last = -1;
        while (true)
        {
            string? f = ActualFile(full);
            long len = -1;
            if (f is not null)
            {
                try { len = new FileInfo(f).Length; }
                catch (IOException) { /* still being written */ }
            }
            if (f is not null && len > 0 && len == last) return f;
            if (DateTime.UtcNow >= deadline) return f;
            last = len;
            Thread.Sleep(len > 0 ? 50 : 100);
        }
    }

    /// <summary>Moves a file that is about to be replaced out of the way, so the export's own file can be proven new.</summary>
    private static string MoveAside(string existing)
    {
        if ((File.GetAttributes(existing) & FileAttributes.ReadOnly) != 0)
            throw new SwException(ErrorCodes.IoError, $"'{existing}' is read-only, so it isn't replaced.", "Choose another file name, or ask the user.");
        string aside = existing + "." + Guid.NewGuid().ToString("N")[..8] + ".swmcp-old";
        try
        {
            File.Move(existing, aside);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SwException(ErrorCodes.IoError, $"'{existing}' can't be replaced: {ex.Message}",
                "It may be open in another program; close it there or choose another file name.");
        }
    }

    private static HashSet<string> DirectoryNames(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir).Take(20_000).Select(p => System.IO.Path.GetFileName(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Names of files that appeared in <paramref name="dir"/> during the call (e.g. one file per component).</summary>
    private static string Strays(string dir, HashSet<string> before, DateTime start)
    {
        try
        {
            var fresh = Directory.EnumerateFiles(dir).Take(20_000)
                .Where(p => !before.Contains(System.IO.Path.GetFileName(p)) && File.GetLastWriteTimeUtc(p) >= start.AddSeconds(-2))
                .Select(p => System.IO.Path.GetFileName(p)).Take(6).ToList();
            return fresh.Count == 0 ? "" : $" New files in the folder: {string.Join(", ", fresh)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
