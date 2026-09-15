using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace WinSentinel.Controls;

/// <summary>
/// Lightweight real-time line graph. Bind <see cref="Values"/> to an
/// <see cref="ObservableCollection{Double}"/>; the control re-renders automatically on every
/// collection change. An optional fill brush shades the area under the line.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineThicknessProperty = DependencyProperty.Register(
        nameof(LineThickness), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush? FillBrush { get => (Brush?)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public double LineThickness { get => (double)GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (Sparkline)d;
        if (e.OldValue is INotifyCollectionChanged oldObservable)
            oldObservable.CollectionChanged -= self.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newObservable)
            newObservable.CollectionChanged += self.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || Values is null) return;

        var points = new List<double>();
        foreach (var raw in Values)
        {
            if (raw is double d) points.Add(d);
            else if (raw is not null && double.TryParse(raw.ToString(), out var parsed)) points.Add(parsed);
        }
        if (points.Count < 2) return;

        double max = Maximum <= 0 ? 1 : Maximum;
        double stepX = w / (points.Count - 1);

        Point Map(int i) => new(i * stepX, h - Math.Clamp(points[i], 0, max) / max * h);

        // Filled area (separate closed geometry so the baseline isn't stroked)
        if (FillBrush is not null)
        {
            var fill = new StreamGeometry();
            using (var ctx = fill.Open())
            {
                ctx.BeginFigure(new Point(0, h), isFilled: true, isClosed: true);
                ctx.LineTo(Map(0), false, false);
                for (int i = 1; i < points.Count; i++) ctx.LineTo(Map(i), false, false);
                ctx.LineTo(new Point((points.Count - 1) * stepX, h), false, false);
            }
            fill.Freeze();
            dc.DrawGeometry(FillBrush, null, fill);
        }

        // Line
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(Map(0), isFilled: false, isClosed: false);
            for (int i = 1; i < points.Count; i++) ctx.LineTo(Map(i), true, false);
        }
        line.Freeze();
        dc.DrawGeometry(null, new Pen(LineBrush, LineThickness) { LineJoin = PenLineJoin.Round }, line);
    }
}
