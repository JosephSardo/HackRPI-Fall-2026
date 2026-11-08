using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;

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
        Run(target, durationMs, delayMs, new CubicEaseOut(), ct, repeat: false,
            new Key(0, 0, new(fromX, fromY, fromScale)),
            new Key(1, 1, new(0, 0, 1)));

    /// <summary>Scales in from small with a little overshoot: for headlines and confirmations.</summary>
    public static Task Pop(Visual target, double delayMs = 0, double durationMs = 520, CancellationToken ct = default) =>
        Run(target, durationMs, delayMs, new BackEaseOut(), ct, repeat: false,
            new Key(0, 0, new(0, 0, 0.6)),
            new Key(0.5, 1, null),
            new Key(1, 1, new(0, 0, 1)));

    /// <summary>A quick side-to-side "no": for clicks that didn't do anything.</summary>
    public static Task Shake(Visual target, CancellationToken ct = default)
    {
        double[] offsets = [0, -9, 8, -6, 5, -3, 0];
        var keys = new Key[offsets.Length];
        for (var i = 0; i < offsets.Length; i++)
            keys[i] = new Key((double)i / (offsets.Length - 1), null, new(offsets[i], 0, 1));
        return Run(target, 420, 0, new LinearEasing(), ct, repeat: false, keys);
    }

    /// <summary>
    /// A ring that grows and fades out from its center, forever (or once with <paramref name="repeat"/> false).
    /// Cancel <paramref name="ct"/> to stop a repeating one, e.g. when the control is removed.
    /// </summary>
    public static Task Ripple(Visual target, double durationMs = 1400, double delayMs = 0, double toScale = 2.2,
                              bool repeat = true, CancellationToken ct = default) =>
        Run(target, durationMs, delayMs, new CubicEaseOut(), ct, repeat,
            new Key(0, 0.9, new(0, 0, 1)),
            new Key(1, 0, new(0, 0, toScale)));

    // ---- The tweening engine
    // Avalonia 12 has no animator for RenderTransform ("No animator registered for the property RenderTransform"
    // crashes the app), so instead of Avalonia keyframe animations these tween opacity and the transform
    // by hand, once per frame on the UI thread.

    /// <summary>A transform: offset in pixels, then uniform scale (around the control's centre).</summary>
    readonly record struct Pose(double X, double Y, double Scale);

    /// <summary>A keyframe: at <c>Cue</c> (0 to 1), these values (null = not set by this key).</summary>
    readonly record struct Key(double Cue, double? Opacity, Pose? Pose);

    static async Task Run(Visual target, double durationMs, double delayMs, Easing easing, CancellationToken ct,
                          bool repeat, params Key[] keys)
    {
        // Hold the first key during the delay (so staggered items start hidden), and the last one after.
        Apply(target, keys, 0, easing);
        try
        {
            if (delayMs > 0) await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
            do
            {
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalMilliseconds < durationMs)
                {
                    ct.ThrowIfCancellationRequested();
                    Apply(target, keys, clock.Elapsed.TotalMilliseconds / durationMs, easing);
                    await Task.Delay(FrameMs, ct);
                }
                Apply(target, keys, 1, easing);
            } while (repeat && !ct.IsCancellationRequested);
        }
        catch (OperationCanceledException) { }
    }

    const int FrameMs = 15;

    static void Apply(Visual target, Key[] keys, double progress, Easing easing)
    {
        var t = easing.Ease(Math.Clamp(progress, 0, 1)); // may overshoot past 1 (BackEaseOut): values extrapolate
        if (Interpolate(keys, t, k => k.Opacity) is { } opacity) target.Opacity = Math.Clamp(opacity, 0, 1);
        if (Interpolate(keys, t, k => k.Pose?.X) is { } x)
        {
            double y = Interpolate(keys, t, k => k.Pose?.Y) ?? 0, scale = Interpolate(keys, t, k => k.Pose?.Scale) ?? 1;
            target.RenderTransform = TransformOperations.Parse(FormattableString.Invariant($"translate({x}px, {y}px) scale({scale})"));
        }
    }

    /// <summary>The value at eased time <paramref name="t"/>, between the keys that set it; null if no key sets it.</summary>
    static double? Interpolate(Key[] keys, double t, Func<Key, double?> value)
    {
        var set = Array.FindAll(keys, k => value(k) is not null);
        if (set.Length == 0) return null;
        if (set.Length == 1 || t <= set[0].Cue) return value(set[0]);
        // The segment t falls in; past the end, keep going along the last one (so an overshooting easing overshoots).
        var i = 0;
        while (i < set.Length - 2 && t >= set[i + 1].Cue) i++;
        var (a, b) = (set[i], set[i + 1]);
        double from = value(a)!.Value, to = value(b)!.Value;
        return from + (to - from) * (t - a.Cue) / (b.Cue - a.Cue);
    }
}
