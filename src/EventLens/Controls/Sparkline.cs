using System.Windows;
using System.Windows.Media;

namespace EventLens.Controls;

/// <summary>Minimal area sparkline for a short series of counts.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<int>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<int>? Values { get => (IReadOnlyList<int>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        if (values is null || values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 4) return;

        double w = ActualWidth, h = ActualHeight - 2;
        double max = Math.Max(1, values.Max());
        double step = w / (values.Count - 1);

        var pts = new Point[values.Count];
        for (int i = 0; i < values.Count; i++)
            pts[i] = new Point(i * step, 1 + h - values[i] / max * h);

        // area
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, h + 1), true, true);
            ctx.PolyLineTo(pts, true, true);
            ctx.LineTo(new Point(w, h + 1), true, true);
        }
        area.Freeze();

        var fill = Stroke.Clone();
        fill.Opacity = 0.18;
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);

        // line
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            ctx.PolyLineTo(pts.Skip(1).ToArray(), true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
