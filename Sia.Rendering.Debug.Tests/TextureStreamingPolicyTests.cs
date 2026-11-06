using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.WebGPU;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class TextureStreamingPolicyTests
{
    [Fact]
    public void ElapsedTimeUploadCreditIsBoundedByTheExplicitFrameLimit()
    {
        double credit = 0;
        Assert.Equal(400u, PbrTextureStreamingPolicy.UploadAllowance(1024, 4000, 0.1, ref credit));
        credit -= 400;
        Assert.Equal(200u, PbrTextureStreamingPolicy.UploadAllowance(1024, 4000, 0.05, ref credit));
        Assert.Equal(600u, PbrTextureStreamingPolicy.UploadAllowance(1024, 4000, 60, ref credit));
        Assert.Equal(1024u, PbrTextureStreamingPolicy.UploadAllowance(1024, 40000, 0.1, ref credit));
        Assert.Equal(16u, PbrTextureStreamingPolicy.UploadAllowance(16, 40000, 0.1, ref credit));
        Assert.Equal(4096u, PbrTextureStreamingPolicy.UploadAllowance(4096, 0, 0, ref credit));
    }

    [Fact]
    public void TransientDemandChangesDoNotCancelAnAdmittedRead()
    {
        var frames = 0;
        Assert.False(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref frames));
        Assert.False(PbrTextureStreamingPolicy.StableObsolete(false, 3, ref frames));
        Assert.Equal(0, frames);
        Assert.False(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref frames));
        Assert.False(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref frames));
        Assert.True(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref frames));
        Assert.True(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref frames));
        Assert.Equal(3, frames);
    }

    [Fact]
    public void DefaultDecodedReservationAdmitsOneFourKRawMip()
    {
        var settings = new PbrTextureStreamingSettings();
        var raw = TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm);
        Assert.Equal(64L * 1024 * 1024, settings.DecodedBytes);
        Assert.Equal(0, PbrTextureStreamingPolicy.MinimumMip(4096, 4096, 6, 8192, settings.DecodedBytes, raw));
    }

    [Theory]
    [InlineData(WGPUTextureFormat.BC7RGBAUnormSrgb)]
    [InlineData(WGPUTextureFormat.BC5RGUnorm)]
    public void LargerCpuDecodedBudgetKeepsFourKCompressedLayersIndependent(WGPUTextureFormat format)
    {
        var layout = TextureBlockLayout.For(format);
        var fullLayer = Enumerable.Range(0, 13).Sum(mip => (long)layout.MipByteLength(
            System.Math.Max(1u, 4096u >> mip), System.Math.Max(1u, 4096u >> mip)));
        Assert.Equal(22_369_648L, fullLayer);
        Assert.Equal(1, PbrTextureStreamingPolicy.ArrayLayerLimit(32L * 1024 * 1024, fullLayer, 256));
        Assert.Equal(1, PbrTextureStreamingPolicy.ArrayLayerLimit(64L * 1024 * 1024, fullLayer, 256));
        Assert.Equal(1, PbrTextureStreamingPolicy.ArrayLayerLimit(256L * 1024 * 1024, fullLayer, 256));
    }

    [Fact]
    public void ArrayGroupingRetainsLowerCpuAndDeviceLayerLimits()
    {
        const long twoKCompressedLayer = 5_592_432;
        Assert.Equal(2, PbrTextureStreamingPolicy.ArrayLayerLimit(16L * 1024 * 1024, twoKCompressedLayer, 256));
        Assert.Equal(5, PbrTextureStreamingPolicy.ArrayLayerLimit(32L * 1024 * 1024, twoKCompressedLayer, 256));
        Assert.Equal(5, PbrTextureStreamingPolicy.ArrayLayerLimit(64L * 1024 * 1024, twoKCompressedLayer, 256));
        Assert.Equal(2, PbrTextureStreamingPolicy.ArrayLayerLimit(64L * 1024 * 1024, twoKCompressedLayer, 2));
    }

    [Fact]
    public void IndependentArrayGroupingDoesNotReduceOneFourKRawMipAdmission()
    {
        var layout = TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm);
        const long decoded = 64L * 1024 * 1024;
        var fullLayer = Enumerable.Range(0, 13).Sum(mip => (long)layout.MipByteLength(
            System.Math.Max(1u, 4096u >> mip), System.Math.Max(1u, 4096u >> mip)));
        Assert.Equal(89_478_484L, fullLayer);
        Assert.Equal((ulong)decoded, layout.MipByteLength(4096, 4096));
        Assert.Equal(1, PbrTextureStreamingPolicy.ArrayLayerLimit(decoded, fullLayer, 256));
        Assert.Equal(0, PbrTextureStreamingPolicy.MinimumMip(4096, 4096, 6, 8192, decoded, layout));
    }

    [Theory]
    [InlineData(WGPUTextureFormat.BC7RGBAUnormSrgb)]
    [InlineData(WGPUTextureFormat.BC5RGUnorm)]
    public void CompressedUpgradeCanReachTheDesiredMipInOneBoundedRequest(WGPUTextureFormat format)
    {
        var request = PbrTextureStreamingPolicy.UpgradeRange(4096, 4096, 13, 6, 0, 6, 1,
            64L * 1024 * 1024, 128ul * 1024 * 1024, TextureBlockLayout.For(format), long.MaxValue);
        Assert.Equal(0, request.Mip);
        Assert.Equal(22_364_160, request.Bytes);
    }

    [Fact]
    public void FourKRawUpgradeSplitsBeforeTheTopMipToPreserveTheDecodedLimit()
    {
        var layout = TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm);
        const long decoded = 64L * 1024 * 1024;
        var first = PbrTextureStreamingPolicy.UpgradeRange(4096, 4096, 13, 6, 0, 6, 1,
            decoded, 128ul * 1024 * 1024, layout, long.MaxValue);
        Assert.Equal((1, 22_347_776L), first);
        var second = PbrTextureStreamingPolicy.UpgradeRange(4096, 4096, 13, first.Mip, 0, first.Mip, 1,
            decoded, 128ul * 1024 * 1024, layout, long.MaxValue);
        Assert.Equal((0, decoded), second);
    }

    [Fact]
    public void PendingDecodedReservationsLimitTheEntireMissingRange()
    {
        var layout = TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm);
        // A sibling read holds 60 MiB until completion, including cancellation.
        var request = PbrTextureStreamingPolicy.UpgradeRange(4096, 4096, 13, 6, 0, 6, 1,
            4L * 1024 * 1024, 128ul * 1024 * 1024, layout, long.MaxValue);
        Assert.Equal((3, 1_376_256L), request);
        Assert.Equal((6, 0L), PbrTextureStreamingPolicy.UpgradeRange(4096, 4096, 13, 6, 0, 6, 1,
            65_535, 128ul * 1024 * 1024, layout, long.MaxValue));
    }

    [Fact]
    public void ReplacementAdmissionCountsEveryLayerAndTheCompleteGpuTail()
    {
        var layout = TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm);
        // A 256-square complete raw tail costs 349,524 bytes per layer.
        const ulong replacement = 349_524ul * 4;
        Assert.Equal((2, 327_680L), PbrTextureStreamingPolicy.UpgradeRange(1024, 1024, 11, 4, 0, 4, 4,
            64L * 1024 * 1024, replacement, layout, long.MaxValue));
        Assert.Equal((3, 65_536L), PbrTextureStreamingPolicy.UpgradeRange(1024, 1024, 11, 4, 0, 4, 4,
            64L * 1024 * 1024, replacement - 1, layout, long.MaxValue));
        Assert.Equal((4, 0L), PbrTextureStreamingPolicy.UpgradeRange(1024, 1024, 11, 4, 0, 4, 4,
            64L * 1024 * 1024, 0, layout, long.MaxValue));
    }

    [Fact]
    public void CoarserLayerCanFillAlreadyAllocatedMipsWithoutAnotherGpuAllocation()
    {
        var layout = TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm);
        // Another layer already owns base mip 2. This layer can reach its own
        // desired mip 3 while the array has no replacement headroom.
        Assert.Equal((3, 65_536L), PbrTextureStreamingPolicy.UpgradeRange(1024, 1024, 11, 4, 3, 2, 4,
            64L * 1024 * 1024, 0, layout, long.MaxValue));
        Assert.Equal((2, 327_680L), PbrTextureStreamingPolicy.UpgradeRange(1024, 1024, 11, 4, 0, 2, 4,
            64L * 1024 * 1024, 0, layout, long.MaxValue));
    }

    [Fact]
    public void OversizedReplacementCannotOverflowIntoAnAdmissibleAllocation()
    {
        Assert.Throws<OverflowException>(() => PbrTextureStreamingPolicy.UpgradeRange(
            536_870_912, 536_870_912, 2, 1, 0, 1, int.MaxValue, long.MaxValue, ulong.MaxValue,
            TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm), long.MaxValue));
    }

    [Theory]
    [InlineData(WGPUTextureFormat.RGBA8Unorm, 4, 327_680L)]
    [InlineData(WGPUTextureFormat.BC7RGBAUnormSrgb, 3, 344_064L)]
    [InlineData(WGPUTextureFormat.BC5RGUnorm, 3, 344_064L)]
    public void FrameInputQuotaPublishesSmallMipBatchesBeforeReadingTheFinestLevels(
        WGPUTextureFormat format, int expectedMip, long expectedBytes)
    {
        var settings = new PbrTextureStreamingSettings();
        var request = PbrTextureStreamingPolicy.UpgradeRange(4096, 4096, 13, 6, 0, 6, 1,
            settings.DecodedBytes, 128ul * 1024 * 1024, TextureBlockLayout.For(format), settings.UploadBytesPerFrame);
        Assert.Equal((expectedMip, expectedBytes), request);
        Assert.True(request.Mip > 0);
        Assert.True(request.Bytes <= settings.UploadBytesPerFrame);
    }

    [Theory]
    [InlineData(WGPUTextureFormat.RGBA8Unorm, 67_108_864L)]
    [InlineData(WGPUTextureFormat.BC7RGBAUnormSrgb, 16_777_216L)]
    public void OneLargeIndependentlyReadableMipStillFitsTheRequestWindow(WGPUTextureFormat format, long nextMipBytes)
    {
        var settings = new PbrTextureStreamingSettings();
        var request = PbrTextureStreamingPolicy.UpgradeRange(4096, 4096, 13, 1, 0, 1, 1,
            settings.DecodedBytes, 128ul * 1024 * 1024, TextureBlockLayout.For(format),
            System.Math.Max(nextMipBytes, settings.UploadBytesPerFrame));
        Assert.Equal((0, nextMipBytes), request);
        Assert.True(request.Bytes > settings.UploadBytesPerFrame);
    }

    [Fact]
    public void FullEightKTextureCanAdmitItsTopMipWithoutDecodingItsWholeReplacement()
    {
        var raw = TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm);
        Assert.Equal(0, PbrTextureStreamingPolicy.MinimumMip(8192, 8192, 7, 8192, 256L * 1024 * 1024, raw));
        Assert.Equal(1, PbrTextureStreamingPolicy.MinimumMip(4096, 4096, 6, 8192, 32L * 1024 * 1024, raw));
    }

    [Fact]
    public void DeviceDimensionRestrictsDetailEvenWhenDecodedBudgetIsLarge()
    {
        var raw = TextureBlockLayout.For(WGPUTextureFormat.RGBA8UnormSrgb);
        Assert.Equal(2, PbrTextureStreamingPolicy.MinimumMip(4096, 2048, 6, 1024, long.MaxValue, raw));
        Assert.Throws<NotSupportedException>(() => PbrTextureStreamingPolicy.MinimumMip(4096, 2048, 0, 1024, long.MaxValue, raw));
    }

    [Fact]
    public void CompressedAdmissionUsesBlockBytesRatherThanFourBytesPerPixel()
    {
        var bc7 = TextureBlockLayout.For(WGPUTextureFormat.BC7RGBAUnormSrgb);
        Assert.Equal(0, PbrTextureStreamingPolicy.MinimumMip(4096, 4096, 6, 8192, 16L * 1024 * 1024, bc7));
        Assert.Equal(1, PbrTextureStreamingPolicy.MinimumMip(4096, 4096, 6, 2048, 16L * 1024 * 1024, bc7));
    }

    [Fact]
    public void ReturningNearViewCancelsAnUnfinishedDowngrade()
    {
        Assert.True(PbrTextureStreamingPolicy.IsObsolete([0, 4], [0, 4], [2, 4]));
        Assert.True(PbrTextureStreamingPolicy.IsObsolete([0, 4], [1, 4], [2, 4]));
        Assert.False(PbrTextureStreamingPolicy.IsObsolete([0, 4], [2, 4], [2, 4]));
    }

    [Fact]
    public void ReturningNearViewAlsoCancelsPhysicalOnlyDowngrade()
    {
        // Unpublished fine storage can be reclaimed without moving logical
        // layer clamps. A returning near view still needs that physical base.
        Assert.True(PbrTextureStreamingPolicy.IsObsolete([3, 4], [1, 4], [3, 4], downgradeMip: 3));
        Assert.False(PbrTextureStreamingPolicy.IsObsolete([3, 4], [3, 4], [3, 4], downgradeMip: 3));
    }

    [Fact]
    public void StillUsefulIntermediateUpgradeSurvivesLargerDetailDemand()
    {
        Assert.False(PbrTextureStreamingPolicy.IsObsolete([4, 4], [0, 6], [3, 4]));
        Assert.True(PbrTextureStreamingPolicy.IsObsolete([4, 4], [4, 4], [3, 4]));
    }

    [Fact]
    public void PartiallyUsefulReadSurvivesACoarserAdmittedTarget()
    {
        // Mips 3..5 are still useful after the original 1..5 read was admitted.
        Assert.False(PbrTextureStreamingPolicy.IsObsolete([6, 4], [3, 4], [1, 4]));
    }

    [Fact]
    public void StableBudgetPlanConvergesWithoutRotatingEquallyImportantLayers()
    {
        int[][] admitted = [[0], [1]];
        for (var frame = 0; frame < 300; frame++) {
            admitted = PbrTextureStreamingPolicy.BudgetTargets([
                new([100, 25, 5], [0], admitted[0], [10]),
                new([100, 25, 5], [0], admitted[1], [10])], 155);
            Assert.Equal(0, admitted[0][0]);
            Assert.Equal(1, admitted[1][0]);
            // The 125-byte final state also reserves 25 bytes for replacement.
            Assert.True(100ul + 25 + 25 <= 155);
        }
    }

    [Fact]
    public void ClearlyMoreImportantNewDemandCanPreemptRetainedDetail()
    {
        var targets = PbrTextureStreamingPolicy.BudgetTargets([
            new([100, 25, 5], [0], [0], [10]),
            new([100, 25, 5], [0], [1], [11])], 155);
        Assert.Equal(1, targets[0][0]);
        Assert.Equal(0, targets[1][0]);
    }

    [Fact]
    public void BudgetTargetsPreserveIndependentLayersAndFullArrayReplacementHeadroom()
    {
        // Costs describe all layers, even when only the first wants fine detail.
        var input = new PbrTextureStreamingPolicy.BudgetInput([400, 100, 20], [0, 2, 1, 2], [2, 2, 2, 2], [10, 0, 1, 1]);
        Assert.Equal(new[] { 0, 2, 1, 2 }, PbrTextureStreamingPolicy.BudgetTargets([input], 500)[0]);
        Assert.Equal(new[] { 1, 2, 1, 2 }, PbrTextureStreamingPolicy.BudgetTargets([input], 499)[0]);
        Assert.Equal(new[] { 2, 2, 2, 2 }, PbrTextureStreamingPolicy.BudgetTargets([input], 119)[0]);
        Assert.Throws<InvalidOperationException>(() => PbrTextureStreamingPolicy.BudgetTargets([input], 19));
    }

    [Fact]
    public void BudgetByteSumCannotWrapIntoAnAdmissiblePlan()
    {
        Assert.Throws<OverflowException>(() => PbrTextureStreamingPolicy.BudgetTargets([
            new([ulong.MaxValue], [0], [0], [1]), new([1], [0], [0], [1])], ulong.MaxValue));
    }

    [Fact]
    public void PriorityHysteresisDistinguishesVisibilityAndStillAdmitsPromotions()
    {
        Assert.Equal(0, PbrTextureStreamingPolicy.PriorityBand(0, 13));
        Assert.True(PbrTextureStreamingPolicy.PriorityBand(1, 0) > 0);
        var band = PbrTextureStreamingPolicy.PriorityBand(64, 0);
        for (var frame = 0; frame < 300; frame++) {
            band = PbrTextureStreamingPolicy.PriorityBand(frame % 2 == 0 ? 63 : 65, band);
            Assert.Equal(13, band);
        }
        Assert.True(PbrTextureStreamingPolicy.PriorityBand(128, band) > band);
        Assert.True(PbrTextureStreamingPolicy.PriorityBand(32, band) < band);
    }

    [Fact]
    public void DemandGraceRetainsResidentDetailForTheExistingIdlePeriod()
    {
        var idle = 0;
        for (var frame = 0; frame < 59; frame++) {
            Assert.Equal(1, PbrTextureStreamingPolicy.DemandMip(6, 1, 0, 60, ref idle));
        }
        Assert.Equal(6, PbrTextureStreamingPolicy.DemandMip(6, 1, 0, 60, ref idle));
        Assert.Equal(0, PbrTextureStreamingPolicy.DemandMip(0, 6, 0, 60, ref idle));
        Assert.Equal(0, idle);
        Assert.Equal(2, PbrTextureStreamingPolicy.DemandMip(0, 6, 2, 60, ref idle));
    }

    [Fact]
    public void OffscreenUninstalledDetailDoesNotBecomeRetainedDemand()
    {
        var idle = 0;
        // Near demand starts a fine read, but the resident tail is still mip 6.
        Assert.Equal(0, PbrTextureStreamingPolicy.DemandMip(0, 6, 0, 60, ref idle));
        var offscreen = PbrTextureStreamingPolicy.DemandMip(6, 6, 0, 60, ref idle);
        Assert.Equal(6, offscreen);
        Assert.Equal(0, idle);
        Assert.True(PbrTextureStreamingPolicy.IsObsolete([6], [offscreen], [3]));
        var obsoleteFrames = 0;
        Assert.False(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref obsoleteFrames));
        Assert.False(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref obsoleteFrames));
        Assert.True(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref obsoleteFrames));
    }

    [Fact]
    public void MixedLayerBudgetPlansRemainFixedAcrossRepeatedPlanning()
    {
        var random = new Random(417);
        for (var sample = 0; sample < 50; sample++) {
            var inputs = Enumerable.Range(0, 8).Select(_ => new PbrTextureStreamingPolicy.BudgetInput(
                [400, 100, 25, 5], [random.Next(4), random.Next(4)], [3, 3], [random.Next(5), random.Next(5)])).ToArray();
            var first = PbrTextureStreamingPolicy.BudgetTargets(inputs, 1000);
            for (var index = 0; index < inputs.Length; index++) inputs[index] = inputs[index] with { Admitted = first[index] };
            var second = PbrTextureStreamingPolicy.BudgetTargets(inputs, 1000);
            for (var index = 0; index < inputs.Length; index++) Assert.Equal(first[index], second[index]);
        }
    }

    [Fact]
    public void CompletedPartialReadTrimsBeforeUploadWithoutCopyingPayloadOrMutatingRead()
    {
        ReadOnlyMemory<byte>[][] read = [
            [new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 }, new byte[] { 4 }, new byte[] { 5 }, default],
            new ReadOnlyMemory<byte>[6]];
        var published = PbrTextureStreamingPolicy.TrimUpgradeMip(6, 3, 1);
        Assert.Equal(3, published);
        var upload = PbrTextureStreamingPolicy.TrimUpload(read, 1, 3, [6, 4], [published, 4]);
        Assert.Equal(4, upload[0].Length);
        Assert.Equal(read[0][2], upload[0][0]);
        Assert.Equal(read[0][4], upload[0][2]);
        Assert.True(upload[0][3].IsEmpty);
        Assert.All(upload[1], mip => Assert.True(mip.IsEmpty));
        Assert.Equal(new byte[] { 1 }, read[0][0].ToArray());
        Assert.Equal(6, read[0].Length);
        Assert.Equal(1, PbrTextureStreamingPolicy.TrimUpgradeMip(6, 0, 1));
        Assert.Equal(6, PbrTextureStreamingPolicy.TrimUpgradeMip(6, 8, 1));
    }

    [Fact]
    public void InPlaceTrimClearsUnneededFinerInputsAndRejectsUninitializedPublication()
    {
        ReadOnlyMemory<byte>[][] read = [[default, new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 }, new byte[] { 4 }, default],
            new ReadOnlyMemory<byte>[6]];
        var upload = PbrTextureStreamingPolicy.TrimUpload(read, 0, 0, [5, 0], [3, 0]);
        Assert.All(upload[0].Take(3), mip => Assert.True(mip.IsEmpty));
        Assert.Equal(read[0][3], upload[0][3]);
        Assert.Equal(read[0][4], upload[0][4]);
        Assert.All(upload[1], mip => Assert.True(mip.IsEmpty));
        Assert.Throws<InvalidOperationException>(() => PbrTextureStreamingPolicy.TrimUpload(read, 0, 0, [5, 0], [0, 0]));
        // A downgrade contains only descriptors: every published level is copied
        // from the old allocation, so no decoded inputs are required.
        var downgrade = PbrTextureStreamingPolicy.TrimUpload([new ReadOnlyMemory<byte>[3]], 3, 3, [0], [3]);
        Assert.All(downgrade[0], mip => Assert.True(mip.IsEmpty));
    }

    [Fact]
    public void PendingAndRetiredStorageCannotWrapTheRemainingBudget()
    {
        Assert.Equal(4ul, PbrTextureStreamingPolicy.AvailableBytes(64, 48, 12));
        Assert.Equal(0ul, PbrTextureStreamingPolicy.AvailableBytes(64, 48, 16));
        Assert.Equal(0ul, PbrTextureStreamingPolicy.AvailableBytes(64, 48, ulong.MaxValue));
        Assert.Equal(0ul, PbrTextureStreamingPolicy.AvailableBytes(64, ulong.MaxValue));
        Assert.Equal(0ul, PbrTextureStreamingPolicy.AvailableBytes(ulong.MaxValue, 1, ulong.MaxValue));
    }

    [Fact]
    public void LargePartialChainRequiresAnExplicitBootstrapAllowance()
    {
        const ulong oneFourKMip = 64ul * 1024 * 1024;
        Assert.Throws<InvalidOperationException>(() => PbrTextureStreamingPolicy.ValidateBootstrap(oneFourKMip, 32ul * 1024 * 1024));
        PbrTextureStreamingPolicy.ValidateBootstrap(oneFourKMip, oneFourKMip);
        Assert.Equal(0, PbrTextureStreamingPolicy.MinimumMip(4096, 4096, 0, 8192,
            32L * 1024 * 1024, TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm)));
    }
}
