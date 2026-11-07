using Sia.Engine.Rendering.Pbr;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class MaterialPipelineRequirementsTests
{
    [Theory]
    [InlineData(false, false, 1u)]
    [InlineData(true, false, 2u)]
    [InlineData(false, true, 4u)]
    [InlineData(true, true, 8u)]
    public void IncludesEveryAuthoredAlphaVariant(bool doubleSided, bool transmission, uint expected)
    {
        foreach (var opacity in new[] { 0f, 1f }) {
            var material = new PbrMaterialAsset(PbrMaterial.Default, DoubleSided: doubleSided,
                AlphaBlend: true, Opacity: opacity, Transmission: transmission ? 1 : 0);
            var required = PbrPipelines.MaterialRequirements(PbrOpaquePath.Visibility, 1, [material]);
            Assert.Equal(expected, required.Transparent);
        }
    }

    [Fact]
    public void UninstancedAndHiddenMaterialRowsRemainPrewarmed()
    {
        // Requirements use the complete immutable material input, without a
        // currently-visible instance list or alpha texture as an extra filter.
        PbrMaterialAsset[] materials = [new(PbrMaterial.Default),
            new(PbrMaterial.Default, AlphaBlend: true),
            new(PbrMaterial.Default, AlphaBlend: true, DoubleSided: true),
            new(PbrMaterial.Default, AlphaBlend: true, Transmission: 1),
            new(PbrMaterial.Default, AlphaBlend: true, DoubleSided: true, Transmission: 1)];
        Assert.Equal(15u, PbrPipelines.MaterialRequirements(PbrOpaquePath.Visibility, 29, materials).Transparent);
        Assert.Equal(15u, PbrPipelines.MaterialRequirements(PbrOpaquePath.ForwardPlus, 29, materials).Transparent);
    }

    [Theory]
    [InlineData(PbrOpaquePath.Visibility, 1, false, true)]
    [InlineData(PbrOpaquePath.Visibility, 2, true, false)]
    [InlineData(PbrOpaquePath.Visibility, 29, true, false)]
    [InlineData(PbrOpaquePath.ForwardPlus, 1, false, false)]
    [InlineData(PbrOpaquePath.ForwardPlus, 29, false, false)]
    public void PrewarmsOnlyTheImmutableBatchPath(PbrOpaquePath path, int groups, bool tiled, bool direct)
    {
        var required = PbrPipelines.MaterialRequirements(path, groups, []);
        Assert.Equal(tiled, required.Tiled); Assert.Equal(direct, required.Direct);
        Assert.Equal(0u, required.Transparent);
    }

    [Fact]
    public void OpaqueDoubleSidedRowsDoNotRequestTransparency()
        => Assert.Equal(0u, PbrPipelines.MaterialRequirements(PbrOpaquePath.Visibility, 1,
            [new(PbrMaterial.Default), new(PbrMaterial.Default, DoubleSided: true)]).Transparent);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsAnInvalidCompletedBatchCount(int groups)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PbrPipelines.MaterialRequirements(PbrOpaquePath.Visibility, groups, []));
}
