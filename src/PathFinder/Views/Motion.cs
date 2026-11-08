using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace PathFinder.Views;

/// <summary>
/// Shared motion for the game screens, so everything moves with the same timing and easing.
/// Use the attached properties from XAML:
/// <list type="bullet">
/// <item><c>views:Motion.EnterDelay="120"</c> fades and rises a control in, <c>120</c> ms after it appears.</item>
/// <item><c>views:Motion.PopDelay="0"</c> scales a control in with a slight overshoot (headlines).</item>
/// <item><c>views:Motion.Stagger="70"</c> on an ItemsControl slides its rows in one after another.</item>
/// </list>
/// Or call <see cref="Enter"/>, <see cref="Pop"/> and <see cref="Shake"/> from code-behind.
/// </summary>
public sealed class Motion
{
    Motion() { }

    public const double DefaultDurationMs = 380;

    public static readonly AttachedProperty<double> EnterDelayProperty =
        AvaloniaProperty.RegisterAttached<Motion, Visual, double>("EnterDelay", -1);

    public static readonly AttachedProperty<double> PopDelayProperty =
        AvaloniaProperty.RegisterAttached<Motion, Visual, double>("PopDelay", -1);

    /// <summary>Milliseconds between one row's entrance and the next.</summary>
    public static readonly AttachedProperty<double> StaggerProperty =
        AvaloniaProperty.RegisterAttached<Motion, ItemsControl, double>("Stagger", -1);

    /// <summary>When the first staggered row enters, in milliseconds.</summary>
    public static readonly AttachedProperty<double> StaggerStartProperty =
        AvaloniaProperty.RegisterAttached<Motion, ItemsControl, double>("StaggerStart", 150);

    public static double GetEnterDelay(Visual v) => v.GetValue(EnterDelayProperty);
    public static void SetEnterDelay(Visual v, double value) => v.SetValue(EnterDelayProperty, value);
    public static double GetPopDelay(Visual v) => v.GetValue(PopDelayProperty);
    public static void SetPopDelay(Visual v, double value) => v.SetValue(PopDelayProperty, value);
    public static double GetStagger(ItemsControl c) => c.GetValue(StaggerProperty);
    public static void SetStagger(ItemsControl c, double value) => c.SetValue(StaggerProperty, value);
    public static double GetStaggerStart(ItemsControl c) => c.GetValue(StaggerStartProperty);
    public static void SetStaggerStart(ItemsControl c, double value) => c.SetValue(StaggerStartProperty, value);

    static Motion()
    {
        EnterDelayProperty.Changed.AddClassHandler<Visual>((v, _) =>
        {
            v.AttachedToVisualTree -= OnAttachedEnter;
            v.AttachedToVisualTree += OnAttachedEnter;
        });
        PopDelayProperty.Changed.AddClassHandler<Visual>((v, _) =>
        {
            v.AttachedToVisualTree -= OnAttachedPop;
            v.AttachedToVisualTree += OnAttachedPop;
        });
        StaggerProperty.Changed.AddClassHandler<ItemsControl>((c, _) =>
        {
            c.ContainerPrepared -= OnContainerPrepared;
            c.ContainerPrepared += OnContainerPrepared;
        });
    }

    static void OnAttachedEnter(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Visual v && GetEnterDelay(v) is var delay and >= 0) _ = Enter(v, delay);
    }

    static void OnAttachedPop(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Visual v && GetPopDelay(v) is var delay and >= 0) _ = Pop(v, delay);
    }

    static void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (sender is not ItemsControl list || GetStagger(list) is not (var step and >= 0)) return;
        _ = Enter(e.Container, GetStaggerStart(list) + e.Index * step, fromX: 28, fromY: 0);
    }

    // ---- The animations themselves

    /// <summary>Fades in while sliding from an offset (and optionally growing) to its place.</summary>
    public static Task Enter(Visual target, double delayMs = 0, double fromX = 0, double fromY = 14,
                             double fromScale = 1, double durationMs = DefaultDurationMs, CancellationToken ct = default) =>
        Run(target, durationMs, delayMs, new CubicEaseOut(), ct,
            Frame(0, (Visual.OpacityProperty, 0d), (Visual.RenderTransformProperty, Transform(fromX, fromY, fromScale))),
            Frame(1, (Visual.OpacityProperty, 1d), (Visual.RenderTransformProperty, Transform(0, 0, 1))));

    /// <summary>Scales in from small with a little overshoot: for headlines and confirmations.</summary>
    public static Task Pop(Visual target, double delayMs = 0, double durationMs = 520, CancellationToken ct = default) =>
        Run(target, durationMs, delayMs, new BackEaseOut(), ct,
            Frame(0, (Visual.OpacityProperty, 0d), (Visual.RenderTransformProperty, Transform(0, 0, 0.6))),
            Frame(0.5, (Visual.OpacityProperty, 1d)),
            Frame(1, (Visual.OpacityProperty, 1d), (Visual.RenderTransformProperty, Transform(0, 0, 1))));

    /// <summary>A quick side-to-side "no": for clicks that didn't do anything.</summary>
    public static Task Shake(Visual target, CancellationToken ct = default)
    {
        double[] offsets = [0, -9, 8, -6, 5, -3, 0];
        var frames = new KeyFrame[offsets.Length];
        for (var i = 0; i < offsets.Length; i++)
            frames[i] = Frame((double)i / (offsets.Length - 1), (Visual.RenderTransformProperty, Transform(offsets[i], 0, 1)));
        return Run(target, 420, 0, new LinearEasing(), ct, frames);
    }

    /// <summary>
    /// A ring that grows and fades out from its center, forever (or once with <paramref name="repeat"/> false).
    /// Cancel <paramref name="ct"/> to stop a repeating one, e.g. when the control is removed.
    /// </summary>
    public static Task Ripple(Visual target, double durationMs = 1400, double delayMs = 0, double toScale = 2.2,
                              bool repeat = true, CancellationToken ct = default) =>
        Run(target, durationMs, delayMs, new CubicEaseOut(), ct, repeat,
            Frame(0, (Visual.OpacityProperty, 0.9d), (Visual.RenderTransformProperty, Transform(0, 0, 1))),
            Frame(1, (Visual.OpacityProperty, 0d), (Visual.RenderTransformProperty, Transform(0, 0, toScale))));

    static ITransform Transform(double x, double y, double scale) =>
        TransformOperations.Parse(FormattableString.Invariant($"translate({x}px, {y}px) scale({scale})"));

    static KeyFrame Frame(double cue, params (AvaloniaProperty Property, object Value)[] values)
    {
        var frame = new KeyFrame { Cue = new Cue(cue) };
        foreach (var (property, value) in values) frame.Setters.Add(new Setter(property, value));
        return frame;
    }

    static Task Run(Visual target, double durationMs, double delayMs, Easing easing, CancellationToken ct,
                    params KeyFrame[] frames) =>
        Run(target, durationMs, delayMs, easing, ct, repeat: false, frames);

    static Task Run(Visual target, double durationMs, double delayMs, Easing easing, CancellationToken ct,
                    bool repeat, params KeyFrame[] frames)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Delay = TimeSpan.FromMilliseconds(delayMs),
            Easing = easing,
            // Hold the first frame during the delay (so staggered items start hidden) and the last one after.
            FillMode = FillMode.Both,
            IterationCount = repeat ? IterationCount.Infinite : new IterationCount(1),
        };
        foreach (var frame in frames) animation.Children.Add(frame);
        return animation.RunAsync(target, ct);
    }
}
