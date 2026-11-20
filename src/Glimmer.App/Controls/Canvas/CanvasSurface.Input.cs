// Ported from App/CanvasView.swift (CanvasNSView: pointer input, gestures, keyboard, paste and drop).
// Windows' input in the Mac's terms: Ctrl is ⌘, Alt is ⌥ (InputModifiers). The mouse, the pen (pressure, tilt,
// rotation, barrel button, eraser end) and one finger all drive the tools through the same three calls; two fingers
// pan, pinch-zoom around their centre and rotate (snapping at right angles); the wheel pans, Shift+wheel pans
// sideways, and Ctrl+wheel or Alt+wheel zooms around the pointer (precision touchpads send their pinch as Ctrl+wheel).
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Controls.Canvas;

public sealed partial class CanvasSurface
{
    private enum Drag { None, Pan, Move, Marquee, Stroke, Lasso, Gradient, Eyedropper, Transform, Crop, Pen, Shape, Guide, Liquify }

    private Drag drag;
    private Vec2 lastPan;
    private bool marqueeShiftFree = true, marqueeOptionFree = true;
    private bool optionHeld;
    /// <summary>Alt was held for a pointer action: its release must not open the menu bar.</summary>
    private bool altUsedForPointer;
    private HwndSource? hwndSource;
    private readonly Dictionary<int, Point> touches = [];
    private TouchGesture? gesture;

    /// <summary>Two fingers on the canvas: where they were last time, and whether they have started to turn the view.</summary>
    private sealed class TouchGesture(Point centroid, double distance, double angle)
    {
        public Point Centroid = centroid;
        public double Distance = distance;
        public double Angle = angle;
        public readonly double StartAngle = angle;
        public bool Rotating;
    }
    private const double RulerThickness = 18;

    // ------------------------------------------------------------------------------------------------- helpers

    private static Vec2 V(Point p) => new(p.X, p.Y);

    private Vec2 Doc(Point p) => Controller!.DocumentPoint(V(p));

    private static InputModifiers Modifiers()
    {
        var k = Keyboard.Modifiers;
        var m = InputModifiers.None;
        if (k.HasFlag(ModifierKeys.Shift)) m |= InputModifiers.Shift;
        if (k.HasFlag(ModifierKeys.Alt)) m |= InputModifiers.Option;
        if (k.HasFlag(ModifierKeys.Control)) m |= InputModifiers.Command;
        return m;
    }

    /// <summary>One pointer press, whatever produced it. barrel: the pen's barrel button, which acts as Alt.</summary>
    private readonly record struct Press(Point Position, InputModifiers Modifiers, int ClickCount, double Pressure, Vec2 Tilt, double Rotation, bool EraserEnd);

    // ------------------------------------------------------------------------------------------------- the tools

    private void PointerDown(Press e)
    {
        if (Session is not { } session || Controller is not { } canvas) return;
        Focus();
        CaptureMouse();
        var p = V(e.Position);
        bool option = e.Modifiers.HasFlag(InputModifiers.Option), shift = e.Modifiers.HasFlag(InputModifiers.Shift);
        if (option) altUsedForPointer = true;
        if (session.EditingText is not null && session.EffectiveTool != ToolID.Type) session.EditingText = null;
        // Rulers: drag out a guide. Existing guides: drag with any tool but the hand (off-canvas deletes).
        if (session.State.Aids.RulersVisible && (p.Y < RulerThickness || p.X < RulerThickness))
        {
            var d = canvas.DocumentPoint(p);
            session.GuideDrag = p.Y < RulerThickness ? new GuideDrag(null, GuideAxis.Horizontal, d.Y) : new GuideDrag(null, GuideAxis.Vertical, d.X);
            drag = Drag.Guide;
            return;
        }
        if (session.TransformSession is null && session.CropSession is null && session.Liquify is null && session.EditingText is null
            && session.EffectiveTool != ToolID.Hand && HitGuide(p) is { } g)
        {
            session.GuideDrag = new GuideDrag(g.Id, g.Axis, g.Position);
            drag = Drag.Guide;
            return;
        }
        if (session.Liquify is not null)
        {
            session.LiquifyDrag(canvas.DocumentPoint(p), true, e.Pressure);
            drag = Drag.Liquify;
            return;
        }
        var doc = canvas.DocumentPoint(p);
        var tool = session.EffectiveTool;
        if (e.EraserEnd && tool is ToolID.Brush or ToolID.Eraser) tool = ToolID.Eraser;   // the pen's other end erases
        switch (tool)
        {
            case ToolID.Move:
                if (e.ClickCount == 2 && session.TextLayerAt(doc) is not null)
                {
                    session.Tool = ToolID.Type;     // double-click on text edits it
                    session.TypeClick(doc);
                    return;
                }
                session.BeginMove(doc);
                drag = Drag.Move;
                break;
            case ToolID.RectangleMarquee or ToolID.EllipseMarquee:
                session.BeginMarquee(doc, e.Modifiers);
                // ⇧ and ⌥ held at the press choose add/subtract; once released they are free to mean "square" and
                // "from the centre" for the rest of the drag.
                marqueeShiftFree = !shift;
                marqueeOptionFree = !option;
                drag = Drag.Marquee;
                break;
            case ToolID.MagicSelect:
                session.AI.MagicSelect(doc, !option);   // click adds a point on the object; ⌥-click marks what to leave out
                break;
            case ToolID.CloneStamp or ToolID.HealingBrush when option:
                session.SetCloneSource(doc);
                break;
            case ToolID.Brush or ToolID.Eraser or ToolID.CloneStamp or ToolID.HealingBrush or ToolID.Dodge or ToolID.Burn
                 or ToolID.Blur or ToolID.Sharpen or ToolID.Smudge or ToolID.PatternStamp:
                session.BeginStroke(new PointerSample(doc, e.Pressure, e.Tilt, e.Rotation), tool == ToolID.Eraser, shift);
                drag = Drag.Stroke;
                break;
            case ToolID.Lasso:
                session.BeginLasso(doc, e.Modifiers);
                drag = Drag.Lasso;
                break;
            case ToolID.PolygonLasso:
                session.PolygonClick(doc, e.Modifiers, e.ClickCount == 2);
                break;
            case ToolID.MagicWand:
                session.MagicWand(doc, e.Modifiers);
                break;
            case ToolID.PaintBucket:
                session.PaintBucket(doc);
                break;
            case ToolID.Gradient:
                session.GradientDrag = new DragSegment(doc, doc);
                drag = Drag.Gradient;
                break;
            case ToolID.Eyedropper:
                session.SampleColor(doc, option);
                drag = Drag.Eyedropper;
                break;
            case ToolID.FreeTransform:
                session.TransformMouseDown(doc, 8, e.Modifiers.HasFlag(InputModifiers.Command));
                drag = Drag.Transform;
                break;
            case ToolID.Crop:
                session.CropMouseDown(session.Snapped(doc), 8);
                drag = Drag.Crop;
                break;
            case ToolID.Type:
                if (session.EditingText is not null)
                {
                    session.EditingText = null;
                    // Clicking another text layer goes straight into it.
                    if (session.TextLayerAt(doc) is not null) session.TypeClick(doc);
                }
                else
                {
                    session.TypeClick(doc);
                }
                break;
            case ToolID.Pen:
                session.PenMouseDown(session.Snapped(doc), 7);
                drag = Drag.Pen;
                break;
            case ToolID.Shape:
            {
                var s = session.Snapped(doc);
                session.ShapeDrag = new DragSegment(s, s);
                drag = Drag.Shape;
                break;
            }
            default:
                BeginPan(p);
                break;
        }
    }

    private void PointerMove(Press e)
    {
        if (Session is not { } session || Controller is not { } canvas) return;
        var p = V(e.Position);
        bool shift = e.Modifiers.HasFlag(InputModifiers.Shift), option = e.Modifiers.HasFlag(InputModifiers.Option);
        switch (drag)
        {
            case Drag.Pan:
                canvas.Pan(p - lastPan);
                lastPan = p;
                break;
            case Drag.Move:
                session.UpdateMove(canvas.DocumentPoint(p));
                break;
            case Drag.Transform:
                session.TransformDrag(canvas.DocumentPoint(p), shift);
                break;
            case Drag.Crop:
                session.CropDrag(session.Snapped(canvas.DocumentPoint(p)), option, shift);
                break;
            case Drag.Pen:
                session.PenDragged(canvas.DocumentPoint(p), option);
                break;
            case Drag.Shape when session.ShapeDrag is { } s0:
            {
                var d = session.Snapped(canvas.DocumentPoint(p));
                if (shift)
                {
                    var v = d - s0.Start;
                    if (session.ShapeKind == ShapeKind.Line)
                    {
                        double a = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
                        d = s0.Start + new Vec2(Math.Cos(a), Math.Sin(a)) * v.Length;
                    }
                    else
                    {
                        double side = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
                        d = s0.Start + new Vec2(v.X < 0 ? -side : side, v.Y < 0 ? -side : side);
                    }
                }
                session.ShapeDrag = s0 with { End = d };
                break;
            }
            case Drag.Liquify:
                session.LiquifyDrag(canvas.DocumentPoint(p), false, e.Pressure);
                break;
            case Drag.Guide when session.GuideDrag is { } g:
            {
                var d = session.Snapped(canvas.DocumentPoint(p));
                session.GuideDrag = g with { Position = g.Axis == GuideAxis.Vertical ? d.X : d.Y };
                break;
            }
            case Drag.Stroke:
                session.ContinueStroke(new PointerSample(canvas.DocumentPoint(p), e.Pressure, e.Tilt, e.Rotation));
                session.HoverPoint = p;
                break;
            case Drag.Lasso:
                session.ExtendLasso(canvas.DocumentPoint(p));
                break;
            case Drag.Gradient when session.GradientDrag is { } g:
            {
                var d = canvas.DocumentPoint(p);
                if (shift)
                {
                    // Constrain to 45° steps.
                    var v = d - g.Start;
                    double a = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
                    d = g.Start + new Vec2(Math.Cos(a), Math.Sin(a)) * v.Length;
                }
                session.GradientDrag = g with { End = d };
                break;
            }
            case Drag.Eyedropper:
                session.SampleColor(canvas.DocumentPoint(p), option);
                break;
            case Drag.Marquee:
            {
                var d = canvas.DocumentPoint(p);
                if (!shift) marqueeShiftFree = true;
                if (!option) marqueeOptionFree = true;
                if (shift && marqueeShiftFree && session.Marquee is { } m)
                {
                    // Shift constrains to a square / circle.
                    var v = d - m.Start;
                    double side = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
                    d = m.Start + new Vec2(v.X < 0 ? -side : side, v.Y < 0 ? -side : side);
                }
                else
                {
                    d = session.Snapped(d);
                }
                if (session.Marquee is { } current) session.Marquee = current with { FromCentre = option && marqueeOptionFree };
                session.UpdateMarquee(d);
                break;
            }
            default:
                session.HoverPoint = p;     // no button down: the brush ring follows the pointer
                break;
        }
    }

    private void PointerUp(Press e)
    {
        if (Session is { } session && Controller is { } canvas)
        {
            switch (drag)
            {
                case Drag.Move: session.EndMove(); break;
                case Drag.Marquee: session.EndMarquee(); break;
                case Drag.Stroke: session.EndStroke(canvas.DocumentPoint(V(e.Position))); break;
                case Drag.Lasso: session.FinishLasso(); break;
                case Drag.Gradient: session.FinishGradient(); break;
                case Drag.Transform: session.TransformMouseUp(); break;
                case Drag.Liquify: session.LiquifyMouseUp(); break;
                case Drag.Crop: session.CropMouseUp(); break;
                case Drag.Pen: session.PenMouseUp(); break;
                case Drag.Shape: session.FinishShape(); break;
                case Drag.Guide when session.GuideDrag is { } g:
                {
                    session.GuideDrag = null;
                    var p = e.Position;
                    double extent = g.Axis == GuideAxis.Vertical ? session.State.Width : session.State.Height;
                    bool outside = p.X < RulerThickness || p.Y < RulerThickness || p.X > ActualWidth || p.Y > ActualHeight
                                   || g.Position < 0 || g.Position > extent;
                    if (g.Id is { } id)
                    {
                        if (outside) session.RemoveGuide(id); else session.MoveGuide(id, Math.Round(g.Position), false);
                    }
                    else if (!outside)
                    {
                        session.AddGuide(g.Axis, Math.Round(g.Position));
                    }
                    break;
                }
            }
        }
        EndDrag();
    }

    private void BeginPan(Vec2 p)
    {
        drag = Drag.Pan;
        lastPan = p;
        Cursor = Cursors.SizeAll;
    }

    private void EndDrag()
    {
        if (drag == Drag.Pan) UpdateCursor();
        drag = Drag.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private Guide? HitGuide(Vec2 p)
    {
        if (Session is not { } session || Controller is not { } canvas) return null;
        var d = canvas.DocumentPoint(p);
        double tolerance = 4 / Math.Max(canvas.Transform.Scale, 0.001);
        return session.State.Aids.Guides.FirstOrDefault(g => Math.Abs((g.Axis == GuideAxis.Vertical ? d.X : d.Y) - g.Position) < tolerance);
    }

    // ------------------------------------------------------------------------------------------------- mouse

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (FromEditor(e)) return;
        if (e.StylusDevice is not null) return;     // promoted from pen or touch: handled there
        if (e.ChangedButton == MouseButton.Middle)
        {
            Focus();
            CaptureMouse();
            BeginPan(V(e.GetPosition(this)));
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            PointerDown(new Press(e.GetPosition(this), Modifiers(), e.ClickCount, 1, Vec2.Zero, 0, false));
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (FromEditor(e)) return;
        if (e.StylusDevice is not null) return;
        PointerMove(new Press(e.GetPosition(this), Modifiers(), 0, 1, Vec2.Zero, 0, false));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (FromEditor(e)) return;
        if (e.StylusDevice is not null) return;
        if (e.ChangedButton is MouseButton.Left or MouseButton.Middle)
        {
            PointerUp(new Press(e.GetPosition(this), Modifiers(), 0, 1, Vec2.Zero, 0, false));
            e.Handled = true;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (drag == Drag.None && Session is { } session) session.HoverPoint = null;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (drag != Drag.None) PointerUp(new Press(e.GetPosition(this), Modifiers(), 0, 1, Vec2.Zero, 0, false));
    }

    // ------------------------------------------------------------------------------------------------- pen

    private bool IsPen(StylusEventArgs e) => e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Stylus;

    protected override void OnStylusDown(StylusDownEventArgs e)
    {
        base.OnStylusDown(e);
        if (FromEditor(e)) return;
        if (!IsPen(e)) return;
        var points = e.GetStylusPoints(this);
        var first = points[0];
        var mods = Modifiers() | (Barrel(first, points) ? InputModifiers.Option : InputModifiers.None);
        PointerDown(new Press(first.ToPoint(), mods, e.TapCount, Pressure(first, points), Tilt(first, points), Twist(first, points), e.Inverted));
        e.Handled = true;   // no promoted mouse events
    }

    protected override void OnStylusMove(StylusEventArgs e)
    {
        base.OnStylusMove(e);
        if (FromEditor(e)) return;
        if (!IsPen(e)) return;
        // Every coalesced point, for smooth strokes at the pen's own rate (the Mac's tabletPoint events).
        var points = e.GetStylusPoints(this);
        foreach (var sp in points)
        {
            var mods = Modifiers() | (Barrel(sp, points) ? InputModifiers.Option : InputModifiers.None);
            PointerMove(new Press(sp.ToPoint(), mods, 0, Pressure(sp, points), Tilt(sp, points), Twist(sp, points), e.Inverted));
        }
        e.Handled = true;
    }

    protected override void OnStylusUp(StylusEventArgs e)
    {
        base.OnStylusUp(e);
        if (FromEditor(e)) return;
        if (!IsPen(e)) return;
        var points = e.GetStylusPoints(this);
        var last = points[^1];
        PointerUp(new Press(last.ToPoint(), Modifiers(), 0, Pressure(last, points), Tilt(last, points), Twist(last, points), e.Inverted));
        e.Handled = true;
    }

    protected override void OnStylusInAirMove(StylusEventArgs e)
    {
        base.OnStylusInAirMove(e);
        if (FromEditor(e)) return;
        if (Session is { } session && drag == Drag.None) session.HoverPoint = V(e.GetPosition(this));
    }

    /// <summary>Pen pressure through the curve in Settings › Tablet (1 for a pen without pressure).</summary>
    private static double Pressure(System.Windows.Input.StylusPoint sp, StylusPointCollection all) =>
        all.Description.HasProperty(StylusPointProperties.NormalPressure)
            ? TabletSettings.Curve(Math.Max(sp.PressureFactor, 0.01), AppServices.Settings)
            : 1;

    /// <summary>Pen tilt as -1…1 per axis in document orientation (the view rotation undone, so the dab leans the way
    /// the pen does on screen); zero when the pen reports none or tilt is off in Settings › Tablet.</summary>
    private Vec2 Tilt(System.Windows.Input.StylusPoint sp, StylusPointCollection all)
    {
        if (!TabletSettings.UseTilt(AppServices.Settings) || !all.Description.HasProperty(StylusPointProperties.XTiltOrientation)
            || !all.Description.HasProperty(StylusPointProperties.YTiltOrientation) || Controller is not { } canvas) return Vec2.Zero;
        // Tilt orientation is in hundredths of a degree, -9000…9000.
        double x = sp.GetPropertyValue(StylusPointProperties.XTiltOrientation) / 9000.0;
        double y = sp.GetPropertyValue(StylusPointProperties.YTiltOrientation) / 9000.0;
        double a = -canvas.Transform.Rotation;
        return new Vec2(x * Math.Cos(a) - y * Math.Sin(a), x * Math.Sin(a) + y * Math.Cos(a));
    }

    /// <summary>Barrel rotation in degrees (TwistOrientation, hundredths of a degree), 0 when not reported.</summary>
    private static double Twist(System.Windows.Input.StylusPoint sp, StylusPointCollection all) =>
        all.Description.HasProperty(StylusPointProperties.TwistOrientation) ? sp.GetPropertyValue(StylusPointProperties.TwistOrientation) / 100.0 : 0;

    private static bool Barrel(System.Windows.Input.StylusPoint sp, StylusPointCollection all) =>
        all.Description.HasProperty(StylusPointProperties.BarrelButton) && sp.GetPropertyValue(StylusPointProperties.BarrelButton) != 0;

    // ------------------------------------------------------------------------------------------------- touch

    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        if (FromEditor(e)) return;
        CaptureTouch(e.TouchDevice);
        var p = e.GetTouchPoint(this).Position;
        touches[e.TouchDevice.Id] = p;
        if (touches.Count == 1)
        {
            PointerDown(new Press(p, Modifiers(), 1, 1, Vec2.Zero, 0, false));
        }
        else if (touches.Count == 2)
        {
            // A second finger turns a tool action into a gesture: finish what the first finger started.
            if (drag != Drag.None) PointerUp(new Press(touches.First().Value, Modifiers(), 0, 1, Vec2.Zero, 0, false));
            var (c, d, a) = Measure();
            gesture = new TouchGesture(c, d, a);
            Controller?.BeginRotationGesture();
        }
        e.Handled = true;
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        base.OnTouchMove(e);
        if (FromEditor(e)) return;
        if (!touches.ContainsKey(e.TouchDevice.Id)) return;
        touches[e.TouchDevice.Id] = e.GetTouchPoint(this).Position;
        if (gesture is { } g && touches.Count >= 2 && Controller is { } canvas)
        {
            var (centroid, distance, angle) = Measure();
            var anchor = V(centroid);
            canvas.Pan(anchor - V(g.Centroid));
            if (g.Distance > 1) canvas.ZoomBy(distance / g.Distance, anchor);
            if (g.Rotating)
            {
                canvas.RotateGesture(Normalize(angle - g.Angle), anchor);
            }
            else if (Math.Abs(Normalize(angle - g.StartAngle)) > 10 * Math.PI / 180)
            {
                // Rotation starts once the fingers have clearly turned, so a pinch doesn't tilt the view; it starts
                // from here, without a jump.
                g.Rotating = true;
            }
            g.Centroid = centroid;
            g.Distance = distance;
            g.Angle = angle;
        }
        else if (touches.Count == 1)
        {
            PointerMove(new Press(touches.Values.First(), Modifiers(), 0, 1, Vec2.Zero, 0, false));
        }
        e.Handled = true;
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        base.OnTouchUp(e);
        if (FromEditor(e)) return;
        ReleaseTouchCapture(e.TouchDevice);
        if (touches.Remove(e.TouchDevice.Id))
        {
            if (gesture is null && drag != Drag.None) PointerUp(new Press(e.GetTouchPoint(this).Position, Modifiers(), 0, 1, Vec2.Zero, 0, false));
            if (touches.Count < 2) gesture = null;
        }
        e.Handled = true;
    }

    private (Point Centroid, double Distance, double Angle) Measure()
    {
        var pts = touches.Values.Take(2).ToArray();
        var c = new Point((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2);
        var d = pts[1] - pts[0];
        return (c, d.Length, Math.Atan2(d.Y, d.X));
    }

    private static double Normalize(double a) => Math.IEEERemainder(a, 2 * Math.PI);

    // ------------------------------------------------------------------------------------------------- wheel

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        HandleWheel(e);
    }

    private void HandleWheel(MouseWheelEventArgs e)
    {
        if (Controller is not { } canvas) return;
        var k = Keyboard.Modifiers;
        double notches = e.Delta / 120.0;
        if (k.HasFlag(ModifierKeys.Control) || k.HasFlag(ModifierKeys.Alt))
        {
            if (k.HasFlag(ModifierKeys.Alt)) altUsedForPointer = true;
            canvas.ZoomBy(Math.Exp(notches * 0.18), V(e.GetPosition(this)));
        }
        else if (k.HasFlag(ModifierKeys.Shift))
        {
            canvas.Pan(new Vec2(notches * WheelStep, 0));
        }
        else
        {
            canvas.Pan(new Vec2(0, notches * WheelStep));
        }
        e.Handled = true;
    }

    /// <summary>DIPs a wheel notch pans: Windows' lines per notch at 16 DIPs a line.</summary>
    private static double WheelStep => Math.Max(1, SystemParameters.WheelScrollLines) * 16;

    // WPF has no event for the horizontal wheel (tilting wheels, two-finger sideways scroll on a touchpad).
    private void AttachWindowHook()
    {
        hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        hwndSource?.AddHook(WindowHook);
        UpdateCursor();
    }

    private void DetachWindowHook()
    {
        hwndSource?.RemoveHook(WindowHook);
        hwndSource = null;
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEHWHEEL = 0x020E, WM_KEYDOWN = 0x0100, VK_RETURN = 0x0D;
        // Enter on the numeric keypad ends text editing, Return doesn't; only the message's extended-key bit tells them apart.
        if (msg == WM_KEYDOWN && wParam.ToInt64() == VK_RETURN) keypadEnter = ((lParam.ToInt64() >> 24) & 1) != 0;
        if (msg == WM_MOUSEHWHEEL && IsVisible && IsMouseOver && Controller is { } canvas)
        {
            int delta = (short)((wParam.ToInt64() >> 16) & 0xffff);
            canvas.Pan(new Vec2(-delta / 120.0 * WheelStep, 0));
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------------------------------------- keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (FromEditor(e)) return;
        if (Session is not { } session || e.Handled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var k = Keyboard.Modifiers;
        bool ctrl = k.HasFlag(ModifierKeys.Control), alt = k.HasFlag(ModifierKeys.Alt), shift = k.HasFlag(ModifierKeys.Shift);
        if (key is Key.LeftAlt or Key.RightAlt) { optionHeld = true; InvalidateVisual(); }
        e.Handled = true;
        if (key == Key.Tab && !ctrl && !alt)
        {
            session.Perform(CommandID.HideInterface);    // as in other editors (also View › Hide Interface); F6 moves the focus on
            return;
        }
        char ch = KeyboardChars.CharFor(key);
        if (ch == '/' && !ctrl && !alt) { session.RequestOmnibarFocus(); return; }
        if (ctrl && !alt && !shift && key == Key.Y) { session.Document.Redo(); return; }      // second Redo, as in the web app
        if (ctrl && !alt && !shift && ch == '=') { session.Perform(CommandID.ZoomIn); return; }
        if (key == Key.Back && (alt || ctrl)) { session.Perform(alt ? CommandID.FillForeground : CommandID.FillBackground); return; }
        if (key == Key.Space)
        {
            if (!e.IsRepeat) session.TemporaryHand = true;
            return;
        }
        if (session.EffectiveTool == ToolID.Move && ArrowOffset(key) is { } arrow)
        {
            int f = shift ? 10 : 1;
            session.NudgeActiveLayer(arrow.X * f, arrow.Y * f);
            return;
        }
        if (!ctrl && !alt && ch != '\0')
        {
            char c = char.ToLowerInvariant(ch);
            switch (c)
            {
                case 'm': session.Tool = session.Tool == ToolID.RectangleMarquee ? ToolID.EllipseMarquee : ToolID.RectangleMarquee; return;
                case 'l': session.Tool = session.Tool == ToolID.Lasso ? ToolID.PolygonLasso : ToolID.Lasso; return;
                case 'o': session.Tool = shift ? ToolID.Burn : ToolID.Dodge; return;   // O is Dodge, ⇧O is Burn
                case 'u' when session.Tool == ToolID.Shape:
                {
                    var kinds = Enum.GetValues<ShapeKind>();
                    session.ShapeKind = kinds[(Array.IndexOf(kinds, session.ShapeKind) + 1) % kinds.Length];
                    return;
                }
                case 'x': session.SwapColors(); return;
                case 'd': session.ResetColors(); return;
                case '[': session.AdjustBrushSize(0.9); return;
                case ']': session.AdjustBrushSize(1.1); return;
            }
            if (ToolTitles.Available.FirstOrDefault(t => t.ShortcutKey() == c, (ToolID)(-1)) is var tool && (int)tool >= 0)
            {
                session.Tool = tool;
                return;
            }
        }
        bool isReturn = key == Key.Enter, isEscape = key == Key.Escape;
        if ((isReturn || isEscape) && session.EffectiveTool == ToolID.MagicSelect && session.AI.MagicPoints.Count > 0)
        {
            // Return keeps the Magic Select result, Esc discards it.
            if (isEscape) session.AI.CancelMagicSelect(); else session.AI.ResetMagicSelect();
            return;
        }
        if (session.Liquify is not null && (isReturn || isEscape))
        {
            if (isReturn) session.CommitLiquify(); else session.CancelLiquify();
            return;
        }
        if (session.TransformSession is not null && (isReturn || isEscape))
        {
            if (isReturn) session.CommitTransform(); else session.CancelTransform();
            session.Tool = ToolID.Move;
            return;
        }
        if (session.CropSession is not null && (isReturn || isEscape))
        {
            if (isReturn) session.ApplyCrop(); else session.CancelCrop();
            session.Tool = ToolID.Move;
            return;
        }
        if (session.EffectiveTool == ToolID.Pen && (isReturn || isEscape))
        {
            if (isEscape) { session.DiscardUnfinishedPath(); session.ActivePathID = null; }
            else session.FinishPathSegment();
            return;
        }
        if (session.Lasso is not null && (isReturn || isEscape))
        {
            if (isEscape) session.CancelLasso(); else session.FinishLasso();
            return;
        }
        if (key is Key.Back or Key.Delete && session.CanPerform(CommandID.ClearPixels))
        {
            session.Perform(CommandID.ClearPixels);
            return;
        }
        if (isEscape && session.State.Selection is not null)
        {
            session.Perform(CommandID.Deselect);    // Esc deselects
            return;
        }
        e.Handled = false;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (FromEditor(e)) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Space && Session is { } session)
        {
            session.TemporaryHand = false;
            e.Handled = true;
        }
        if (key is Key.LeftAlt or Key.RightAlt)
        {
            optionHeld = false;
            InvalidateVisual();
            // Alt held for an Alt-click or Alt-wheel must not open the menu bar when it is let go.
            if (altUsedForPointer) e.Handled = true;
            altUsedForPointer = false;
        }
    }

    private static (int X, int Y)? ArrowOffset(Key key) => key switch
    {
        Key.Left => (-1, 0), Key.Right => (1, 0), Key.Down => (0, 1), Key.Up => (0, -1), _ => null,
    };

    internal bool OptionHeld => optionHeld;

    // ------------------------------------------------------------------------------------------------- cursor

    /// <summary>The pointer per tool (CanvasNSView.resetCursorRects). The open and closed hands are Windows' move
    /// cursor until the app has its own cursors.</summary>
    private void UpdateCursor()
    {
        Cursor = Session?.EffectiveTool switch
        {
            ToolID.Hand => Cursors.SizeAll,
            ToolID.Type => Cursors.IBeam,
            ToolID.Move or ToolID.FreeTransform or null => Cursors.Arrow,
            _ => Cursors.Cross,
        };
    }

    // ------------------------------------------------------------------------------------------------- drop

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (FromEditor(e)) return;
        e.Effects = CanAccept(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (FromEditor(e)) return;
        if (Session is not { } session) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            session.Place(files.Where(ImageFiles.IsImage).ToArray());
        }
        else if (e.Data.GetData("PNG") is System.IO.MemoryStream png)
        {
            session.PlaceImageData(png.ToArray(), "Pasted Layer");
        }
        e.Handled = true;
    }

    private static bool CanAccept(IDataObject data) =>
        (data.GetData(DataFormats.FileDrop) is string[] files && files.Any(ImageFiles.IsImage)) || data.GetDataPresent("PNG");
}

/// <summary>The character a key types on the current keyboard layout, without modifiers, as the Mac's
/// charactersIgnoringModifiers gives it: single-key shortcuts follow the layout ("/" and "[" are where the person's
/// keyboard has them).</summary>
internal static class KeyboardChars
{
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public static char CharFor(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return (char)('a' + (key - Key.A));
        if (key is >= Key.D0 and <= Key.D9) return (char)('0' + (key - Key.D0));
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return '\0';
        try
        {
            uint ch = MapVirtualKey((uint)vk, 2 /* MAPVK_VK_TO_CHAR */) & 0x7FFFFFFF;   // the high bit marks a dead key
            return ch is > 0 and < 0xFFFF ? (char)ch : '\0';
        }
        catch (DllNotFoundException)
        {
            return '\0';
        }
    }
}

/// <summary>Which files Glimmer opens and places.</summary>
internal static class ImageFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif", ".psd", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2",
    };

    public static bool IsImage(string path) => Extensions.Contains(System.IO.Path.GetExtension(path));
}
