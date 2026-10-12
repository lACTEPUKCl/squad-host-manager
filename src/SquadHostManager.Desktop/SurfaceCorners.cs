using System.Windows;
using System.Windows.Media;

namespace SquadHostManager.Desktop;

public static class SurfaceCorners
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(SurfaceCorners), new PropertyMetadata(0d, Changed));
    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);
    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);
    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement surface) return;
        surface.SizeChanged -= Resized;
        surface.SizeChanged += Resized;
        Update(surface);
    }
    private static void Resized(object sender, SizeChangedEventArgs args) => Update((FrameworkElement)sender);
    private static void Update(FrameworkElement surface)
    {
        var radius = GetRadius(surface);
        surface.Clip = new RectangleGeometry(new Rect(0, 0, surface.ActualWidth, surface.ActualHeight), radius, radius);
    }
}
