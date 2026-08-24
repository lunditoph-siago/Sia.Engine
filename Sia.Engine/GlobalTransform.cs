using Sia.Math;

namespace Sia.Engine;

public record struct GlobalTransform(AffineTransform Affine)
{
    public static GlobalTransform Identity => new(AffineTransform.Identity);
}
