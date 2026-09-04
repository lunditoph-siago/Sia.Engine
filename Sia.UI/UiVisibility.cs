using Sia;

namespace Sia.UI;

public static class UiVisibility
{
    public static bool IsVisible(Entity entity)
    {
        while (entity.IsValid) {
            if (entity.Contains<Visibility>()
                && entity.Get<Visibility>() != Visibility.Visible) {
                return false;
            }
            if (!entity.Contains<UiChildOf>()) {
                return true;
            }
            entity = entity.Get<UiChildOf>().Parent;
        }
        return false;
    }
}
