using System.Collections.ObjectModel;
using System.Text;
using Sia.Math;

namespace Sia.Engine.Rendering;

/// <summary>Behavior below zero for a static display exponent or gamma curve.</summary>
public enum DisplayTransformNegativeStyle : byte { Clamp, Mirror, PassThrough, Linear }

/// <summary>An independently owned RGB float32 cube, red coordinate fastest. Sampling is tetrahedral.</summary>
public sealed class DisplayTransformLut3D
{
    private readonly float[] _rgb;
    public int Size { get; }
    public ReadOnlySpan<float> Rgb32F => _rgb;
    public long OwnedBytes => (long)_rgb.Length * sizeof(float);

    internal DisplayTransformLut3D(int size, ReadOnlySpan<float> rgb)
    {
        if (size < 2 || size > DisplayTransformData.MaximumLutSize || rgb.Length != checked(size * size * size * 3))
            throw new ArgumentException("Display LUT shape exceeds its admitted RGB cube.", nameof(size));
        foreach (var value in rgb)
            if (!float.IsFinite(value)) throw new ArgumentException("Display LUT must contain finite values.", nameof(rgb));
        Size = size; _rgb = rgb.ToArray();
    }
}

/// <summary>One validated static operation. Factories accept data, never shader text or GPU resources.</summary>
public sealed class DisplayTransformStage
{
    internal enum Operation : byte { Matrix = 1, Log, Exponent, MonCurve, Lut3D }
    internal Operation Kind { get; }
    internal float3 A { get; }
    internal float3 B { get; }
    internal float3 C { get; }
    internal float3 D { get; }
    internal float Base { get; }
    internal bool Inverse { get; }
    internal DisplayTransformNegativeStyle NegativeStyle { get; }
    internal DisplayTransformLut3D? Table { get; }

    private DisplayTransformStage(Operation kind, float3 a = default, float3 b = default, float3 c = default,
        float3 d = default, float @base = 0, bool inverse = false,
        DisplayTransformNegativeStyle negativeStyle = DisplayTransformNegativeStyle.Clamp, DisplayTransformLut3D? table = null)
    { Kind = kind; A = a; B = b; C = c; D = d; Base = @base; Inverse = inverse; NegativeStyle = negativeStyle; Table = table; }

    public static DisplayTransformStage Matrix(float3 row0, float3 row1, float3 row2, float3 offset)
    {
        Finite(row0); Finite(row1); Finite(row2); Finite(offset);
        return new(Operation.Matrix, row0, row1, row2, offset);
    }

    /// <summary>Forward clamps to the minimum positive normal float32 before log; inverse raises base to the input.</summary>
    public static DisplayTransformStage Log(float @base, bool inverse = false)
    {
        if (!float.IsFinite(@base) || @base <= 0 || @base == 1)
            throw new ArgumentOutOfRangeException(nameof(@base));
        return new(Operation.Log, @base: @base, inverse: inverse);
    }

    public static DisplayTransformStage Exponent(float3 exponent,
        DisplayTransformNegativeStyle negativeStyle = DisplayTransformNegativeStyle.Clamp, bool inverse = false)
    {
        Finite(exponent); Style(negativeStyle);
        if (exponent.x <= 0 || exponent.y <= 0 || exponent.z <= 0 || negativeStyle == DisplayTransformNegativeStyle.Linear
            || !float.IsFinite(1 / exponent.x) || !float.IsFinite(1 / exponent.y) || !float.IsFinite(1 / exponent.z))
            throw new ArgumentOutOfRangeException(nameof(exponent));
        return new(Operation.Exponent, exponent, inverse: inverse, negativeStyle: negativeStyle);
    }

    /// <summary>A differentiable gamma/offset toe; its breakpoint is derived from those parameters, not an IEC preset.</summary>
    public static DisplayTransformStage MonCurve(float3 gamma, float3 offset, bool inverse = false,
        DisplayTransformNegativeStyle negativeStyle = DisplayTransformNegativeStyle.Linear)
    {
        Finite(gamma); Finite(offset); Style(negativeStyle);
        for (var i = 0; i < 3; i++) {
            var g = Component(gamma, i); var o = Component(offset, i);
            if (g <= 1 || o <= 0) throw new ArgumentOutOfRangeException(nameof(gamma));
            var (encoded, linear, slope) = Toe(g, o);
            if (!float.IsFinite(encoded) || !float.IsFinite(linear) || !float.IsFinite(slope) || encoded <= 0 || linear <= 0 || slope <= 0)
                throw new ArgumentOutOfRangeException(nameof(gamma), "Gamma toe cannot be represented by finite positive float32 constants.");
        }
        return new(Operation.MonCurve, gamma, offset, inverse: inverse, negativeStyle: negativeStyle);
    }

    public static DisplayTransformStage Lut3D(int size, ReadOnlySpan<float> rgb)
        => new(Operation.Lut3D, table: new(size, rgb));

    internal static (float Encoded, float Linear, float Slope) Toe(float gamma, float offset)
    {
        var encoded = (double)offset / (gamma - 1d);
        var linear = System.Math.Pow((encoded + offset) / (1d + offset), gamma);
        return ((float)encoded, (float)linear, (float)(linear / encoded));
    }

    internal static float Component(float3 value, int index) => index switch { 0 => value.x, 1 => value.y, _ => value.z };
    private static void Finite(float3 value)
    { if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z)) throw new ArgumentException("Display stage must contain finite parameters."); }
    private static void Style(DisplayTransformNegativeStyle style)
    { if (!Enum.IsDefined(style)) throw new ArgumentOutOfRangeException(nameof(style)); }
}

/// <summary>Immutable scene-authored display mapping, specialized at pipeline creation. Output is display encoded.</summary>
public sealed partial class DisplayTransformData
{
    public const int MaximumStages = 32;
    public const int MaximumLookupTables = 8;
    public const int MaximumLutSize = 129;
    public const ulong MaximumEncodedBytes = 64ul * 1024 * 1024;
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    public string InputColorSpace { get; }
    public string OutputColorSpace { get; }
    public string SourceRecipeHash { get; }
    public IReadOnlyList<DisplayTransformStage> Stages { get; }
    public IReadOnlyList<DisplayTransformLut3D> LookupTables { get; }
    /// <summary>Owned float32 lookup payload bytes, excluding CLR object overhead.</summary>
    public long OwnedBytes { get; }

    private DisplayTransformData(string input, string output, string hash, DisplayTransformStage[] stages, DisplayTransformLut3D[] tables)
    {
        InputColorSpace = input; OutputColorSpace = output; SourceRecipeHash = hash;
        Stages = new ReadOnlyCollection<DisplayTransformStage>(stages);
        LookupTables = new ReadOnlyCollection<DisplayTransformLut3D>(tables);
        OwnedBytes = tables.Sum(table => table.OwnedBytes);
    }

    public static DisplayTransformData Create(string inputColorSpace, string outputColorSpace, string sourceRecipeHash,
        IEnumerable<DisplayTransformStage> stages, ulong maximumBytes = MaximumEncodedBytes)
    {
        Name(inputColorSpace); Name(outputColorSpace); ArgumentNullException.ThrowIfNull(stages);
        if (sourceRecipeHash is null || sourceRecipeHash.Length != 64 || sourceRecipeHash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Display recipe requires a SHA-256 hexadecimal identity.", nameof(sourceRecipeHash));
        if (maximumBytes == 0 || maximumBytes > MaximumEncodedBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var copied = new List<DisplayTransformStage>(); var tables = new List<DisplayTransformLut3D>();
        // Header16 + checksum32 + two UInt16 name lengths + recipe32 + UInt32 stage count.
        ulong admitted = 88ul + (ulong)s_utf8.GetByteCount(inputColorSpace) + (ulong)s_utf8.GetByteCount(outputColorSpace);
        foreach (var stage in stages) {
            ArgumentNullException.ThrowIfNull(stage);
            if (copied.Count == MaximumStages) throw new ArgumentException("Display transform exceeds its static stage cap.", nameof(stages));
            // Every occurrence is encoded, including a repeated immutable LUT stage.
            admitted += stage.Kind switch {
                DisplayTransformStage.Operation.Matrix => 52ul,
                DisplayTransformStage.Operation.Log => 8ul,
                DisplayTransformStage.Operation.Exponent => 16ul,
                DisplayTransformStage.Operation.MonCurve => 28ul,
                DisplayTransformStage.Operation.Lut3D => 8ul,
                _ => throw new InvalidOperationException("Unknown static display stage.")
            };
            if (stage.Table is { } table) {
                if (tables.Count == MaximumLookupTables) throw new ArgumentException("Display transform exceeds its lookup table cap.", nameof(stages));
                tables.Add(table); admitted += (ulong)table.OwnedBytes;
            }
            if (admitted > maximumBytes) throw new ArgumentException("Display transform exceeds its byte admission.", nameof(maximumBytes));
            copied.Add(stage);
        }
        if (copied.Count == 0) throw new ArgumentException("Display transform requires at least one explicit stage.", nameof(stages));
        return new(inputColorSpace, outputColorSpace, sourceRecipeHash.ToUpperInvariant(), copied.ToArray(), tables.ToArray());
    }

    private static void Name(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (s_utf8.GetByteCount(name) > 256 || name.Any(char.IsControl)) throw new ArgumentException("Display color-space identity is too long or contains controls.", nameof(name));
    }
}
