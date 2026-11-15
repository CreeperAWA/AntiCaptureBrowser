using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace AntiCaptureBrowser.Controls;

/// <summary>
/// 平滑滚动 ScrollViewer：鼠标滚轮滚动使用缓动动画（仿 Chrome / Edge），
/// 拖动滚动条、键盘翻页仍保持即时定位。
/// </summary>
public class SmoothScrollViewer : ScrollViewer
{
    // 代理偏移属性：动画驱动它，再同步到真实滚动位置
    private static readonly DependencyProperty ProxyOffsetProperty =
        DependencyProperty.Register(nameof(ProxyOffset), typeof(double), typeof(SmoothScrollViewer),
            new PropertyMetadata(0.0, (d, e) => ((SmoothScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    private double ProxyOffset
    {
        get => (double)GetValue(ProxyOffsetProperty);
        set => SetValue(ProxyOffsetProperty, value);
    }

    private bool _isAnimating;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (ScrollableHeight <= 0)
        {
            base.OnMouseWheel(e);
            return;
        }

        e.Handled = true;
        double target = Math.Clamp(VerticalOffset - e.Delta, 0, ScrollableHeight);

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
        {
            From = VerticalOffset,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        _isAnimating = true;
        animation.Completed += (_, _) =>
        {
            _isAnimating = false;
            ProxyOffset = VerticalOffset; // 动画结束后让代理值对齐真实偏移
        };
        BeginAnimation(ProxyOffsetProperty, animation);
    }

    /// <summary>非动画引起的滚动（拖滚动条、键盘等）时，保持代理值与真实偏移一致。</summary>
    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);
        if (_isAnimating) return;
        if (Math.Abs(ProxyOffset - VerticalOffset) > 0.001)
        {
            // 先清除动画再直接赋值，避免与动画冲突
            BeginAnimation(ProxyOffsetProperty, null);
            ProxyOffset = VerticalOffset;
        }
    }
}
