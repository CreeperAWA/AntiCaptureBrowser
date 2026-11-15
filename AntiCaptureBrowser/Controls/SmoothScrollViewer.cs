using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AntiCaptureBrowser.Controls;

/// <summary>
/// 平滑滚动 ScrollViewer：鼠标滚轮通过逐帧指数平滑逼近目标位置（帧率无关、无动画重启抖动），
/// 拖动滚动条、键盘翻页保持即时定位。
/// </summary>
public class SmoothScrollViewer : ScrollViewer
{
    /// <summary>平滑系数：值越大收敛越快。</summary>
    private const double SmoothingRate = 14.0;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private double _targetOffset;
    private bool _hooked;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (ScrollableHeight <= 0)
        {
            base.OnMouseWheel(e);
            return;
        }

        e.Handled = true;
        if (!_hooked)
        {
            _targetOffset = VerticalOffset;
            CompositionTarget.Rendering += OnRenderFrame;
            _hooked = true;
        }
        _targetOffset = Math.Clamp(_targetOffset - e.Delta, 0, ScrollableHeight);
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        double dt = Math.Max(_stopwatch.Elapsed.TotalSeconds, 0.001);
        _stopwatch.Restart();

        _targetOffset = Math.Clamp(_targetOffset, 0, Math.Max(0, ScrollableHeight));
        double diff = _targetOffset - VerticalOffset;

        if (Math.Abs(diff) < 0.5)
        {
            ScrollToVerticalOffset(_targetOffset);
            DetachRenderingHook();
            return;
        }

        // 帧率无关的指数平滑：平滑系数换算到当前帧时长
        double smoothing = 1 - Math.Exp(-dt * SmoothingRate);
        ScrollToVerticalOffset(VerticalOffset + diff * smoothing);
    }

    /// <summary>非滚轮引起的滚动（拖动滚动条、键盘等）时同步目标位置，避免互相打架。</summary>
    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);
        if (!_hooked)
        {
            _targetOffset = VerticalOffset;
        }
    }

    private void DetachRenderingHook()
    {
        if (_hooked)
        {
            CompositionTarget.Rendering -= OnRenderFrame;
            _hooked = false;
        }
    }

    public SmoothScrollViewer()
    {
        Unloaded += (_, _) => DetachRenderingHook(); // 控件卸载时解除渲染钩子
    }
}
