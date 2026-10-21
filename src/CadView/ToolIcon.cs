using System.Windows;
using System.Windows.Media;

namespace TinyCAD;

public static class ToolIcon
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached("IsActive", typeof(bool), typeof(ToolIcon), new PropertyMetadata(false));
    public static void SetIsActive(DependencyObject target, bool value) => target.SetValue(IsActiveProperty, value);
    public static bool GetIsActive(DependencyObject target) => (bool)target.GetValue(IsActiveProperty);
    public static readonly DependencyProperty DataProperty = DependencyProperty.RegisterAttached("Data", typeof(Geometry), typeof(ToolIcon));
    public static void SetData(DependencyObject target, Geometry value) => target.SetValue(DataProperty, value);
    public static Geometry GetData(DependencyObject target) => (Geometry)target.GetValue(DataProperty);
}
