using System.Runtime.InteropServices;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.WebGPU;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class TextureCoverageStorageTests
{
    private static PbrTextureData Texture(bool srgb,uint size=64)
    {
        var levels=new List<ReadOnlyMemory<byte>>();
        for(var width=size;;width=System.Math.Max(1u,width>>1)) {
            levels.Add(new byte[checked((int)(width*width*4))]);
            if(width==1) break;
        }
        return PbrTextureData.Create(size,size,srgb,levels.ToArray());
    }

    [Fact]
    public void CoverageColorAndPairedNormalAreIndependentWithoutDuplicatingSharedOpaqueSources()
    {
        var color=Texture(true); var normal=Texture(false); var opaqueColor=Texture(true); var emission=Texture(true);
        PbrMaterialAsset[] materials=[new(PbrMaterial.Default,BaseColor:color,Normal:normal,AlphaBlend:true,Emissive:emission),
            new(PbrMaterial.Default,BaseColor:opaqueColor,Normal:normal),new(PbrMaterial.Default,BaseColor:color,Normal:normal,AlphaBlend:true)];
        var sources=new Dictionary<PbrTextureData,PbrSceneStream.TextureInfo>(ReferenceEqualityComparer.Instance) {
            [color]=new(2048,2048,12,5),[normal]=new(2048,2048,12,5),[opaqueColor]=new(2048,2048,12,5),[emission]=new(2048,2048,12,5)
        };
        var selected=PbrTextureStreamingPolicy.IndependentCoverageSources(materials,sources);
        Assert.Equal(2,selected.Count); Assert.Contains(color,selected); Assert.Contains(normal,selected);
        Assert.DoesNotContain(opaqueColor,selected); Assert.DoesNotContain(emission,selected);
        Assert.Same(normal,materials[1].Normal);
        // Byte-equal independently authored objects retain their own identities.
        // Classification never broadens via content equality or duplicates a
        // shared normal just because an opaque material also references it.
        Assert.NotSame(color,opaqueColor); Assert.Equal(color.MipLevels.Span[0].ToArray(),opaqueColor.MipLevels.Span[0].ToArray());
        Assert.Equal(color.Sampler,opaqueColor.Sampler); Assert.Equal(color.Format,opaqueColor.Format);
    }

    [Fact]
    public void WholeAndTailZeroSourcesKeepOriginalGroupingWhileOrdinaryShapesRetainTheirLayerCap()
    {
        var whole=Texture(true); var constant=Texture(false,2); var refinable=Texture(true);
        PbrMaterialAsset[] materials=[new(PbrMaterial.Default,BaseColor:whole,Normal:constant,AlphaBlend:true),
            new(PbrMaterial.Default,BaseColor:refinable,AlphaBlend:true)];
        var sources=new Dictionary<PbrTextureData,PbrSceneStream.TextureInfo>(ReferenceEqualityComparer.Instance) {
            [constant]=new(2,2,2,0),[refinable]=new(2048,2048,12,5)
        };
        var selected=PbrTextureStreamingPolicy.IndependentCoverageSources(materials,sources);
        Assert.Same(refinable,Assert.Single(selected));
        Assert.Empty(PbrTextureStreamingPolicy.IndependentCoverageSources(materials,null));
        // Complete 2k BC7 tail: ordinary batching keeps five layers in the
        // existing 32MiB grouping allowance. Coverage needs only one layer's
        // replacement allocation; this changes storage, not source dimensions.
        const long full=5592432;
        Assert.Equal(5,PbrTextureStreamingPolicy.ArrayLayerLimit(64L*1024*1024,full,256));
        var groups=PbrTextureStreamingPolicy.PartitionCoverageChunk([whole,constant,refinable],selected).ToArray();
        Assert.Equal(new[]{whole,constant},groups[0]); Assert.Same(refinable,Assert.Single(groups[1]));
        Assert.Equal(0,PbrTextureStreamingPolicy.ArrayLayerLimit(64L*1024*1024,full,0));
    }

    [Fact]
    public void ExtractingCoveragePreservesOriginalOrdinaryCohortsInsteadOfRechunkingTheShape()
    {
        var sources=Enumerable.Range(0,7).Select(_=>Texture(true)).ToArray();
        var independent=new HashSet<PbrTextureData>(ReferenceEqualityComparer.Instance){sources[1],sources[5]};
        var original=sources.Chunk(5).ToArray();
        var groups=original.SelectMany(chunk=>PbrTextureStreamingPolicy.PartitionCoverageChunk(chunk,independent)).ToArray();
        Assert.Equal(new[]{sources[0],sources[2],sources[3],sources[4]},groups[0]);
        Assert.Same(sources[1],Assert.Single(groups[1]));
        Assert.Same(sources[6],Assert.Single(groups[2])); Assert.Same(sources[5],Assert.Single(groups[3]));
        // The previous shape-wide regroup pulled source6 into the first opaque
        // array, allowing unrelated source0's fine mip to allocate source6 too.
        Assert.DoesNotContain(sources[6],groups[0]);
        Assert.Equal(sources.Length,groups.Sum(group=>group.Length));
        Assert.True(groups.SelectMany(group=>group).ToHashSet(ReferenceEqualityComparer.Instance).SetEquals(sources));
        var noCoverage=new HashSet<PbrTextureData>(ReferenceEqualityComparer.Instance);
        Assert.Same(original[0],Assert.Single(PbrTextureStreamingPolicy.PartitionCoverageChunk(original[0],noCoverage)));
    }

    [Fact]
    public void IsolatedRefinementFitsExistingBudgetWhereAnUnrelatedFiveLayerArrayCannot()
    {
        // Captured Bux targets: original2k, resident mip3, wanted mip1.
        // Existing replacement headroom and all priorities remain applicable.
        var bc7=TextureBlockLayout.For(WGPUTextureFormat.BC7RGBAUnormSrgb);
        const long available=6035300;
        var isolated=PbrTextureStreamingPolicy.UpgradeRange(2048,2048,12,3,1,3,1,64L*1024*1024,(ulong)available,bc7,64L*1024*1024);
        var shared=PbrTextureStreamingPolicy.UpgradeRange(2048,2048,12,3,1,3,5,64L*1024*1024,(ulong)available,bc7,64L*1024*1024);
        Assert.Equal(1,isolated.Mip); Assert.Equal(2,shared.Mip);
        // The publication descriptors retain the established GPU-tail reuse:
        // only new mip1/2 input is uploaded, mip3+ remains resident and copied.
        var finer=new byte[16]; var next=new byte[4];
        ReadOnlyMemory<byte>[][] read=[[finer,next,default,default]];
        var trimmed=PbrTextureStreamingPolicy.TrimUpload(read,1,1,[3],[1]);
        Assert.True(MemoryMarshal.TryGetArray(trimmed[0][0],out var first)); Assert.Same(finer,first.Array);
        Assert.True(MemoryMarshal.TryGetArray(trimmed[0][1],out var second)); Assert.Same(next,second.Array);
        Assert.Equal(read[0][0],trimmed[0][0]); Assert.Equal(read[0][1],trimmed[0][1]);
        Assert.True(trimmed[0][2].IsEmpty&&trimmed[0][3].IsEmpty);
    }

    [Fact]
    public void MoreResourceBindingGroupsReuseExistingMaterialPipelineRequirements()
    {
        var color=Texture(true); var normal=Texture(false);
        PbrMaterialAsset[] materials=[new(PbrMaterial.Default,BaseColor:color,Normal:normal,AlphaBlend:true),
            new(PbrMaterial.Default,Normal:normal),new(PbrMaterial.Default,BaseColor:color,AlphaBlend:true,DoubleSided:true)];
        var before=PbrPipelines.MaterialRequirements(PbrOpaquePath.Visibility,80,materials);
        var after=PbrPipelines.MaterialRequirements(PbrOpaquePath.Visibility,91,materials);
        Assert.Equal(before,after); Assert.True(after.Tiled); Assert.False(after.Direct);
        Assert.Equal(3u,after.Transparent);
    }
}
