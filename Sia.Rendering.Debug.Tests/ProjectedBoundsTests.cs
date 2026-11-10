using Sia.Engine.Rendering;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class ProjectedBoundsTests
{
    [Fact]
    public void ActualBistroBushUsesClippedScreenExtentInsteadOfGeometryErrorAmplification()
    {
        // Canonical mesh aa4ce6a0-48b2-52a0-8600-c59978747cc5, object
        // Bistro_Research_Exterior_bux_hedge_ball_small_3947. Bounds are its
        // authored Float32 world AABB. VP is the captured production Frame512
        // prefix from canonical-medium-preview02 (1280x720, entrance camera).
        var bounds = new Aabb(new(-2.30814433f,.767108202f,.957956851f), new(-.617581844f,2.40959263f,2.57089448f));
        var vp = new float4x4(
            new(.745039046f,.143299386f,.907117009f,.906814635f),
            new(-2.64909588e-8f,3.15655041f,-.0499543734f,-.0499377213f),
            new(1.61412871f,-.06614317f,-.418701142f,-.418561578f),
            new(3.39879942f,-7.47427130f,8.54494667f,8.64209938f));
        var actual = ProjectedBounds.PixelSize(bounds,vp,1280,720);
        Assert.InRange(actual,359f,359.1f);
        var wrongErrorBound = ProjectedGeometryError.ProjectError(bounds,math.length(bounds.Max-bounds.Min),vp,1280,720);
        Assert.InRange(wrongErrorBound,2098f,2100f);
        Assert.True(wrongErrorBound > actual*5);
        // Independent corner projection is exact for this wholly depth-inside
        // fixture; no streaming policy, production clipping helper or mip math.
        Assert.InRange(MathF.Abs(actual-CornerExtent(bounds,vp,1280,720)),0,.001f);
    }

    [Fact]
    public void FrustumExcludedNearCrossingAndCameraContainingBoxesStayBounded()
    {
        var vp=float4x4.PerspectiveFov(MathF.PI/2,1,.1f,10);
        Assert.Equal(0,ProjectedBounds.PixelSize(new(new(-1,-1,1),new(1,1,3)),vp,200,200));
        Assert.Equal(0,ProjectedBounds.PixelSize(new(new(-1,-1,-100),new(1,1,-20)),vp,200,200));
        Assert.Equal(0,ProjectedBounds.PixelSize(new(new(10,-1,-2),new(12,1,-1)),vp,200,200));
        // Near-plane crossing entirely to the left must not become a full-screen
        // request merely because some of its original corners lie behind w=0.
        Assert.Equal(0,ProjectedBounds.PixelSize(new(new(-100,-1,-.2f),new(-10,1,.2f)),vp,200,200));
        Assert.InRange(ProjectedBounds.PixelSize(new(new(-.025f,-.025f,-.2f),new(.025f,.025f,.2f)),vp,200,200),49.99f,50.01f);
        Assert.Equal(200,ProjectedBounds.PixelSize(new(new(-1,-1,-1),new(1,1,1)),vp,200,200));
        Assert.Equal(200,ProjectedBounds.PixelSize(new(new(-100,-100,-100),new(100,100,100)),vp,200,200));
    }

    [Fact]
    public void ClipsViewportAndHonorsAspectRotationAndProjectiveScale()
    {
        var projection=float4x4.PerspectiveFov(MathF.PI/2,2,.1f,100);
        var bounds=new Aabb(new(-2,-1,-4),new(2,1,-2));
        Assert.InRange(ProjectedBounds.PixelSize(bounds,projection,400,200),199.99f,200.01f);
        var side=new Aabb(new(1,-.5f,-2),new(10,.5f,-1));
        Assert.InRange(ProjectedBounds.PixelSize(side,projection,400,200),149.99f,150.01f);
        // A 45-degree camera turn changes the projected rectangle; its vertical
        // extent clips at 200 while its horizontal extent is about 212 pixels.
        var view=new float4x4(new(.70710677f,.70710677f,0,0),new(-.70710677f,.70710677f,0,0),new(0,0,1,0),new(0,0,0,1));
        var turned=math.mul(projection,view);
        Assert.InRange(ProjectedBounds.PixelSize(bounds,turned,400,200),212.12f,212.15f);
        var scaled=new float4x4(projection.c0*8192,projection.c1*8192,projection.c2*8192,projection.c3*8192);
        Assert.InRange(MathF.Abs(ProjectedBounds.PixelSize(bounds,scaled,400,200)-ProjectedBounds.PixelSize(bounds,projection,400,200)),0,.0001f);
        Assert.Equal(0,ProjectedBounds.PixelSize(bounds,projection,0,200));
        // A degenerate/nonfinite projection is never allowed to emit Inf demand.
        var invalid=projection; invalid.c0.x=float.NaN;
        Assert.Equal(400,ProjectedBounds.PixelSize(bounds,invalid,400,200));
    }

    private static float CornerExtent(Aabb bounds,float4x4 vp,uint width,uint height)
    {
        double loX=double.PositiveInfinity,loY=double.PositiveInfinity,hiX=double.NegativeInfinity,hiY=double.NegativeInfinity;
        for(var i=0;i<8;i++) {
            var p=math.mul(vp,new float4((i&1)==0?bounds.Min.x:bounds.Max.x,(i&2)==0?bounds.Min.y:bounds.Max.y,(i&4)==0?bounds.Min.z:bounds.Max.z,1));
            loX=System.Math.Min(loX,(double)p.x/p.w); hiX=System.Math.Max(hiX,(double)p.x/p.w); loY=System.Math.Min(loY,(double)p.y/p.w); hiY=System.Math.Max(hiY,(double)p.y/p.w);
        }
        return (float)System.Math.Max(System.Math.Max(0,System.Math.Min(1,hiX)-System.Math.Max(-1,loX))*width*.5,
            System.Math.Max(0,System.Math.Min(1,hiY)-System.Math.Max(-1,loY))*height*.5);
    }
}
