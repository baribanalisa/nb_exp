using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using UserControl = System.Windows.Controls.UserControl;
namespace NeuroBureau.Experiment.Controls;

public partial class TimeRangeSlider : UserControl
{
    public event EventHandler? RangeChanged;

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(TimeRangeSlider),
            new PropertyMetadata(0d, OnAnyChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(TimeRangeSlider),
            new PropertyMetadata(1d, OnAnyChanged));

    public static readonly DependencyProperty StartValueProperty =
        DependencyProperty.Register(nameof(StartValue), typeof(double), typeof(TimeRangeSlider),
            new FrameworkPropertyMetadata(0d, OnStartEndChanged, CoerceStart));

    public static readonly DependencyProperty EndValueProperty =
        DependencyProperty.Register(nameof(EndValue), typeof(double), typeof(TimeRangeSlider),
            new FrameworkPropertyMetadata(1d, OnStartEndChanged, CoerceEnd));

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double StartValue
    {
        get => (double)GetValue(StartValueProperty);
        set => SetValue(StartValueProperty, value);
    }

    public double EndValue
    {
        get => (double)GetValue(EndValueProperty);
        set => SetValue(EndValueProperty, value);
    }

    public TimeRangeSlider()
    {
        InitializeComponent();

        Loaded += (_, __) => UpdateVisuals();
        PART_Canvas.SizeChanged += (_, __) => UpdateVisuals();

        StartThumb.DragDelta += StartThumb_DragDelta;
        EndThumb.DragDelta += EndThumb_DragDelta;
    }

    private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TimeRangeSlider)d;
        c.CoerceValue(StartValueProperty);
        c.CoerceValue(EndValueProperty);
        c.UpdateVisuals();
    }

    private static void OnStartEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TimeRangeSlider)d;
        c.UpdateVisuals();

        if (c.IsLoaded)
            c.RangeChanged?.Invoke(c, EventArgs.Empty);
    }

    private static object CoerceStart(DependencyObject d, object baseValue)
    {
        var c = (TimeRangeSlider)d;
        var v = (double)baseValue;

        var min = c.Minimum;
        var max = c.Maximum;
        if (max < min) (min, max) = (max, min);

        v = Math.Max(min, Math.Min(v, max));
        v = Math.Min(v, c.EndValue);
        return v;
    }

    private static object CoerceEnd(DependencyObject d, object baseValue)
    {
        var c = (TimeRangeSlider)d;
        var v = (double)baseValue;

        var min = c.Minimum;
        var max = c.Maximum;
        if (max < min) (min, max) = (max, min);

        v = Math.Max(min, Math.Min(v, max));
        v = Math.Max(v, c.StartValue);
        return v;
    }

    private void StartThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var deltaValue = DxToValue(e.HorizontalChange);
        StartValue = StartValue + deltaValue; // Coerce сделает Start<=End и в пределах min/max
    }

    private void EndThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var deltaValue = DxToValue(e.HorizontalChange);
        EndValue = EndValue + deltaValue;
    }

    private double DxToValue(double dx)
    {
        var trackLen = GetTrackLen();
        if (trackLen <= 0) return 0;

        var range = Maximum - Minimum;
        if (Math.Abs(range) < 1e-12) return 0;

        return dx / trackLen * range;
    }

    private double GetTrackLen()
    {
        // чтобы ручки не “вылезали” за края
        var w = PART_Canvas.ActualWidth;
        var thumbW = StartThumb.ActualWidth > 0 ? StartThumb.ActualWidth : StartThumb.Width;
        return Math.Max(0, w - thumbW);
    }

    private void UpdateVisuals()
    {
        var w = PART_Canvas.ActualWidth;
        if (w <= 0) return;

        var thumbW = StartThumb.ActualWidth > 0 ? StartThumb.ActualWidth : StartThumb.Width;
        var trackLeft = thumbW / 2.0;
        var trackRight = w - thumbW / 2.0;
        var trackLen = Math.Max(0, trackRight - trackLeft);

        // трек во всю ширину (визуально), но позиционирование считаем с запасом под ручки
        Track.Width = Math.Max(0, w);
        Canvas.SetLeft(Track, 0);

        if (Maximum <= Minimum || trackLen <= 0)
            return;

        double ns = (StartValue - Minimum) / (Maximum - Minimum);
        double ne = (EndValue - Minimum) / (Maximum - Minimum);

        ns = Math.Max(0, Math.Min(1, ns));
        ne = Math.Max(0, Math.Min(1, ne));

        var xStartCenter = trackLeft + ns * trackLen;
        var xEndCenter = trackLeft + ne * trackLen;

        Canvas.SetLeft(StartThumb, xStartCenter - thumbW / 2.0);
        Canvas.SetLeft(EndThumb, xEndCenter - thumbW / 2.0);

        var selLeft = xStartCenter;
        var selWidth = Math.Max(0, xEndCenter - xStartCenter);
        Selection.Width = selWidth;
        Canvas.SetLeft(Selection, selLeft);
    }
}
