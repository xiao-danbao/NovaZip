using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NovaZip.Controls
{
    public sealed class ProgressRing : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(ProgressRing),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
            nameof(Thickness), typeof(double), typeof(ProgressRing),
            new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ShowTextProperty = DependencyProperty.Register(
            nameof(ShowText), typeof(bool), typeof(ProgressRing),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
            nameof(TrackBrush), typeof(Brush), typeof(ProgressRing),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush? TrackBrush
        {
            get => (Brush?)GetValue(TrackBrushProperty);
            set => SetValue(TrackBrushProperty, value);
        }

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public double Thickness
        {
            get => (double)GetValue(ThicknessProperty);
            set => SetValue(ThicknessProperty, value);
        }

        public bool ShowText
        {
            get => (bool)GetValue(ShowTextProperty);
            set => SetValue(ShowTextProperty, value);
        }

        private static readonly Pen TrackPen = CreatePen(new SolidColorBrush(Color.FromRgb(0x1B, 0x25, 0x37)), 6);
        private static readonly LinearGradientBrush ArcBrush = CreateBrush();

        private static Pen CreatePen(Brush brush, double thickness)
        {
            var pen = new Pen(brush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            return pen;
        }

        private static LinearGradientBrush CreateBrush()
        {
            var brush = new LinearGradientBrush(
                Color.FromRgb(0x38, 0xBD, 0xF8),
                Color.FromRgb(0x8B, 0x5C, 0xF6),
                new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            return brush;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 8) return;

            var thickness = Thickness;
            var radius = (size - thickness) / 2 - 1;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);

            dc.DrawEllipse(null, new Pen(TrackBrush ?? TrackPen.Brush, thickness), center, radius, radius);

            var value = Math.Max(0, Math.Min(100, Value));
            if (value >= 99.95)
            {
                // 360° 圆弧的起点与终点重合，WPF 会判定为退化图形而不绘制，满进度时直接画整圆
                var fullPen = new Pen(ArcBrush, thickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                dc.DrawEllipse(null, fullPen, center, radius, radius);
            }
            else if (value > 0.05)
            {
                var angle = value / 100.0 * 360.0;
                var start = new Point(center.X, center.Y - radius);
                var endAngle = (angle - 90) * Math.PI / 180.0;
                var end = new Point(center.X + radius * Math.Cos(endAngle), center.Y + radius * Math.Sin(endAngle));

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(start, false, false);
                    ctx.ArcTo(end, new Size(radius, radius), 0, angle > 180,
                        SweepDirection.Clockwise, true, false);
                }
                geometry.Freeze();

                var pen = new Pen(ArcBrush, thickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                dc.DrawGeometry(null, pen, geometry);
            }

            if (ShowText)
            {
                var text = value.ToString("0", CultureInfo.InvariantCulture) + "%";
                var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal,
                        FontWeights.SemiBold, FontStretches.Normal),
                    15, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
            }
        }
    }
}
