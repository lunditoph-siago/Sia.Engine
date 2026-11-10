using System.Globalization;
using System.Text;
using Sia.Math;

namespace Sia.Engine.Rendering;

/// <summary>Pure static specialization. The caller owns the declared unfilterable float32 3D textures.</summary>
public static class DisplayTransformShaderSource
{
    public static string Compile(DisplayTransformData data, string moduleName, uint group = 0, uint firstBinding = 3)
    {
        ArgumentNullException.ThrowIfNull(data); ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        if (moduleName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '/' or '-'))
            || firstBinding > uint.MaxValue - (uint)data.LookupTables.Count)
            throw new ArgumentException("Invalid display module identity or binding range.", nameof(moduleName));
        var result = new StringBuilder(); result.Append("#define_import_path ").AppendLine(moduleName);
        for (var i = 0; i < data.LookupTables.Count; i++) {
            result.Append("@group(").Append(group).Append(") @binding(").Append(firstBinding + (uint)i)
                .Append(") var rendering_display_table_").Append(i).AppendLine(": texture_3d<f32>;");
            Lut(result, i, data.LookupTables[i].Size);
        }
        result.AppendLine("fn rendering_display_transform(color: vec3<f32>) -> vec3<f32> {");
        result.AppendLine("    var v = color;");
        var table = 0;
        foreach (var stage in data.Stages) {
            switch (stage.Kind) {
                case DisplayTransformStage.Operation.Matrix:
                    result.Append("    v = vec3<f32>(dot(").Append(Vector(stage.A)).Append(", v), dot(")
                        .Append(Vector(stage.B)).Append(", v), dot(").Append(Vector(stage.C)).Append(", v)) + ")
                        .Append(Vector(stage.D)).AppendLine(";"); break;
                case DisplayTransformStage.Operation.Log:
                    if (stage.Inverse) result.Append("    v = pow(vec3<f32>(").Append(Number(stage.Base)).AppendLine("), v);");
                    else result.Append("    v = log2(max(v, vec3<f32>(1.1754943508222875e-38))) / vec3<f32>(")
                        .Append(Number(MathF.Log2(stage.Base))).AppendLine(");");
                    break;
                case DisplayTransformStage.Operation.Exponent: Power(result, stage); break;
                case DisplayTransformStage.Operation.MonCurve: MonCurve(result, stage); break;
                case DisplayTransformStage.Operation.Lut3D:
                    result.Append("    v = rendering_display_lut_").Append(table++).AppendLine("(v);"); break;
                default: throw new InvalidOperationException("Unknown static display stage.");
            }
        }
        result.AppendLine("    return v;").AppendLine("}");
        return result.ToString();
    }

    private static string Number(float value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : text + ".0";
    }

    private static string Vector(float3 v) => $"vec3<f32>({Number(v.x)}, {Number(v.y)}, {Number(v.z)})";

    private static void Power(StringBuilder result, DisplayTransformStage stage)
    {
        var exponent = stage.Inverse ? new float3(1 / stage.A.x, 1 / stage.A.y, 1 / stage.A.z) : stage.A;
        var expression = $"pow(max(v, vec3<f32>(0.0)), {Vector(exponent)})";
        var value = stage.NegativeStyle switch {
            DisplayTransformNegativeStyle.Clamp => expression,
            DisplayTransformNegativeStyle.Mirror => $"sign(v) * pow(abs(v), {Vector(exponent)})",
            DisplayTransformNegativeStyle.PassThrough => $"select(v, {expression}, v >= vec3<f32>(0.0))",
            _ => throw new InvalidOperationException("Invalid exponent negative style.")
        };
        result.Append("    v = ").Append(value).AppendLine(";");
    }

    private static void MonCurve(StringBuilder result, DisplayTransformStage stage)
    {
        var (ex, lx, sx) = DisplayTransformStage.Toe(stage.A.x, stage.B.x);
        var (ey, ly, sy) = DisplayTransformStage.Toe(stage.A.y, stage.B.y);
        var (ez, lz, sz) = DisplayTransformStage.Toe(stage.A.z, stage.B.z);
        var encoded = Vector(new(ex, ey, ez)); var linear = Vector(new(lx, ly, lz)); var slope = Vector(new(sx, sy, sz));
        var onePlusOffset = Vector(new(1 + stage.B.x, 1 + stage.B.y, 1 + stage.B.z));
        var gamma = Vector(stage.A); var offset = Vector(stage.B);
        var inverseGamma = Vector(new(1 / stage.A.x, 1 / stage.A.y, 1 / stage.A.z));
        result.AppendLine("    {");
        result.Append("        let x = ").Append(stage.NegativeStyle switch {
            DisplayTransformNegativeStyle.Mirror => "abs(v)",
            DisplayTransformNegativeStyle.Clamp => "max(v, vec3<f32>(0.0))",
            _ => "v"
        }).AppendLine(";");
        if (stage.Inverse)
            result.Append("        let mapped = select(").Append(onePlusOffset).Append(" * pow(max(x, vec3<f32>(0.0)), ")
                .Append(inverseGamma).Append(") - ").Append(offset).Append(", x / ").Append(slope).Append(", x <= ").Append(linear).AppendLine(");");
        else
            result.Append("        let mapped = select(pow(max((x + ").Append(offset).Append(") / ").Append(onePlusOffset)
                .Append(", vec3<f32>(0.0)), ").Append(gamma).Append("), x * ").Append(slope).Append(", x <= ").Append(encoded).AppendLine(");");
        result.Append("        v = ").Append(stage.NegativeStyle switch {
            DisplayTransformNegativeStyle.Mirror => "sign(v) * mapped",
            DisplayTransformNegativeStyle.PassThrough => "select(v, mapped, v >= vec3<f32>(0.0))",
            _ => "mapped"
        }).AppendLine(";");
        result.AppendLine("    }");
    }

    private static void Lut(StringBuilder result, int table, int size)
    {
        result.Append("fn rendering_display_lut_").Append(table).AppendLine("(color: vec3<f32>) -> vec3<f32> {");
        result.Append("    let q = clamp(color, vec3<f32>(0.0), vec3<f32>(1.0)) * vec3<f32>(").Append(size - 1).AppendLine(".0);");
        result.Append("    let base = min(vec3<i32>(floor(q)), vec3<i32>(").Append(size - 2).AppendLine("));");
        result.AppendLine("    let f = q - vec3<f32>(base);");
        result.AppendLine("    var o1: vec3<i32>; var o2: vec3<i32>; var weights: vec3<f32>;");
        result.AppendLine("    if (f.x >= f.y) {");
        result.AppendLine("        if (f.y >= f.z) { o1 = vec3<i32>(1,0,0); o2 = vec3<i32>(1,1,0); weights = f.xyz; }");
        result.AppendLine("        else if (f.x >= f.z) { o1 = vec3<i32>(1,0,0); o2 = vec3<i32>(1,0,1); weights = f.xzy; }");
        result.AppendLine("        else { o1 = vec3<i32>(0,0,1); o2 = vec3<i32>(1,0,1); weights = f.zxy; }");
        result.AppendLine("    } else {");
        result.AppendLine("        if (f.z >= f.y) { o1 = vec3<i32>(0,0,1); o2 = vec3<i32>(0,1,1); weights = f.zyx; }");
        result.AppendLine("        else if (f.z >= f.x) { o1 = vec3<i32>(0,1,0); o2 = vec3<i32>(0,1,1); weights = f.yzx; }");
        result.AppendLine("        else { o1 = vec3<i32>(0,1,0); o2 = vec3<i32>(1,1,0); weights = f.yxz; }");
        result.AppendLine("    }");
        for (var i = 0; i < 4; i++) {
            var offset = i switch { 0 => "base", 1 => "base + o1", 2 => "base + o2", _ => "base + vec3<i32>(1)" };
            result.Append("    let t").Append(i).Append(" = textureLoad(rendering_display_table_").Append(table).Append(", ")
                .Append(offset).AppendLine(", 0).rgb;");
        }
        result.AppendLine("    return t0 + (t1 - t0) * weights.x + (t2 - t1) * weights.y + (t3 - t2) * weights.z;");
        result.AppendLine("}");
    }
}
