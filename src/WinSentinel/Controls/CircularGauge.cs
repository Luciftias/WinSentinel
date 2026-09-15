using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinSentinel.Helpers;

namespace WinSentinel.Controls;

/// <summary>
/// Self-contained circular progress gauge drawn in <see cref="OnRender"/> — no template,
/// no third-party dependency. Shows a track ring, a value arc that grows clockwise from
/// 12 o'clock, the value as a centred percentage, and an optional caption beneath it.
/// The arc eases to new values (respecting the motion preferences) for a premium feel.
/// </summary>
public sealed class CircularGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(CircularGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    /// <summary>Eased display value driven by <see cref="Value"/> animations.</summary>
    public static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.Register(
        nameof(AnimatedValue), typeof(double), typeof(CircularGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(CircularGauge),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(CircularGauge),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(CircularGauge),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(33, 38, 48)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(CircularGauge),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionBrushProperty = DependencyProperty.Register(
        nameof(CaptionBrush), typeof(Brush), typeof(CircularGauge),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(139, 148, 158)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(CircularGauge),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(CircularGauge),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double AnimatedValue { get => (double)GetValue(AnimatedValueProperty); private set => SetValue(AnimatedValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush CaptionBrush { get => (Brush)GetValue(CaptionBrushProperty); set => SetValue(CaptionBrushProperty, value); }
    public double RingThickness { get => (double)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (CircularGauge)d;
        double target = gauge.Value;

        if (!Motion.UseAnimations)
        {
            gauge.AnimatedValue = target;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(280)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        gauge.BeginAnimation(AnimatedValueProperty, animation);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double size = Math.Min(w, h);
        var center = new Point(w / 2, h / 2);
        double radius = (size - RingThickness) / 2 - 2;
        if (radius <= 0) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Track ring
        var trackPen = new Pen(TrackBrush, RingThickness);
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        // Value arc (eased display value)
        double fraction = Maximum <= 0 ? 0 : Math.Clamp(AnimatedValue / Maximum, 0, 1);
        if (fraction >= 0.999)
        {
            var fullPen = new Pen(RingBrush, RingThickness);
            dc.DrawEllipse(null, fullPen, center, radius, radius);
        }
        else if (fraction > 0)
        {
            const double startAngle = -90; // 12 o'clock
            double sweep = 360 * fraction;
            var figure = new PathFigure
            {
                StartPoint = PointOnCircle(center, radius, startAngle),
                IsClosed = false,
                IsFilled = false
            };
            figure.Segments.Add(new ArcSegment
            {
                Point = PointOnCircle(center, radius, startAngle + sweep),
                Size = new Size(radius, radius),
                IsLargeArc = sweep > 180,
                SweepDirection = SweepDirection.Clockwise
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            var arcPen = new Pen(RingBrush, RingThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawGeometry(null, arcPen, geometry);
        }

        // Centre value text
        var valueText = new FormattedText(
            $"{AnimatedValue:0}%",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            Math.Max(12, size * 0.22), TextBrush, dpi);
        dc.DrawText(valueText, new Point(center.X - valueText.Width / 2, center.Y - valueText.Height / 2 - size * 0.05));

        // Caption
        if (!string.IsNullOrEmpty(Caption))
        {
            var caption = new FormattedText(
                Caption,
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                Math.Max(9, size * 0.095), CaptionBrush, dpi);
            dc.DrawText(caption, new Point(center.X - caption.Width / 2, center.Y + valueText.Height / 2 - size * 0.04));
        }
    }

    private static Point PointOnCircle(Point c, double r, double angleDegrees)
    {
        double a = angleDegrees * Math.PI / 180.0;
        return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
    }
}
