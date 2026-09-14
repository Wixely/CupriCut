using System.Globalization;
using CupriCut.Services;
using SkiaSharp;

namespace CupriCut.Gui;

/// <summary>
/// Draws the marked regions onto the previewed frame.
///
/// <para><b>Composited into the frame, not layered over it in the DOM.</b> An annotation's
/// rectangle is in frame coordinates — 0–1 of the picture — and that is the only space in which it
/// means anything. Drawing it as positioned elements over the stage would need those coordinates
/// mapped into window space and kept in step with every resize and letterbox, which is a second
/// chance to get the same arithmetic wrong. Painting into the frame keeps one mapping, the one the
/// pointer already goes through in reverse.</para>
///
/// <para>Only annotations belonging to the moment on screen are drawn. An annotation marks a
/// problem at a time, so showing all of them at once would say nothing about when.</para>
/// </summary>
public static class AnnotationOverlay
{
    /// <summary>How long after its timestamp an annotation stays on the frame. Long enough to find
    /// by scrubbing without having to land on the exact frame it was drawn at.</summary>
    public const double VisibleForSeconds = 1.0;

    private static readonly SKColor Marked = new(0xD9, 0x64, 0x2A);
    private static readonly SKColor Resolved = new(0x4E, 0xC9, 0xB0);
    private static readonly SKColor Drawing = new(0xF4, 0xF6, 0xFB);

    /// <summary>Is this annotation on screen at <paramref name="t"/>?</summary>
    public static bool VisibleAt(Annotation annotation, double t) =>
        t >= annotation.Time - 1e-9 && t < annotation.Time + VisibleForSeconds;

    /// <summary>Paint the annotations that belong to this moment, and the rectangle being dragged.</summary>
    public static void Draw(SKCanvas canvas, int width, int height, OverlayState state)
    {
        foreach (var mark in state.Marks)
        {
            if (!VisibleAt(mark.Annotation, state.Time)) continue;
            DrawRegion(canvas, width, height, mark, state.Time);
        }

        if (state.Dragging) DrawDrag(canvas, width, height, state);
    }

    private static void DrawRegion(SKCanvas canvas, int width, int height, OverlayMark mark, double t)
    {
        var a = mark.Annotation;
        var rect = SKRect.Create(
            (float)(a.X * width), (float)(a.Y * height),
            (float)(a.W * width), (float)(a.H * height));

        var colour = a.Status == AnnotationStatus.Resolved ? Resolved : Marked;

        // It fades over its second on screen, so "this has just appeared" reads differently from
        // "this is about to go" while scrubbing.
        var age = Math.Clamp((t - a.Time) / VisibleForSeconds, 0, 1);
        var alpha = (byte)(255 * (1 - 0.55 * age));

        // A dark halo first: a thin orange line vanishes on an orange composition.
        using var halo = new SKPaint
        {
            Color = new SKColor(0, 0, 0, (byte)(alpha * 0.5)),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(4, width / 300f),
            IsAntialias = true,
        };
        canvas.DrawRect(rect, halo);

        using var stroke = new SKPaint
        {
            Color = colour.WithAlpha(alpha),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2, width / 640f),
            IsAntialias = true,
        };
        canvas.DrawRect(rect, stroke);

        if (mark.Selected)
        {
            using var wash = new SKPaint { Color = colour.WithAlpha((byte)(alpha * 0.12)) };
            canvas.DrawRect(rect, wash);
        }

        DrawLabel(canvas, width, rect, Label(a), colour.WithAlpha(alpha), alpha);
    }

    private static string Label(Annotation a)
    {
        var t = a.Time.ToString("0.###", CultureInfo.InvariantCulture);
        var note = a.Note.Length <= 44 ? a.Note : a.Note[..43] + "…";
        return $"{t}s  {note}";
    }

    private static void DrawLabel(SKCanvas canvas, int width, SKRect rect, string text, SKColor colour, byte alpha)
    {
        var size = Math.Clamp(width / 60f, 11f, 22f);
        using var font = new SKFont(SKTypeface.Default, size);
        using var ink = new SKPaint { Color = new SKColor(0x0A, 0x0D, 0x13, alpha), IsAntialias = true };
        using var plate = new SKPaint { Color = colour, IsAntialias = true };

        var textWidth = font.MeasureText(text);
        var padding = size * 0.45f;
        var boxHeight = size + padding * 1.4f;

        // Above the region, unless that would be off the top of the frame.
        var top = rect.Top - boxHeight - 3;
        if (top < 0) top = Math.Min(rect.Bottom + 3, canvas.DeviceClipBounds.Bottom - boxHeight);

        var box = SKRect.Create(rect.Left, top, textWidth + padding * 2, boxHeight);
        canvas.DrawRoundRect(box, 3, 3, plate);
        canvas.DrawText(text, box.Left + padding, box.Top + size, SKTextAlign.Left, font, ink);
    }

    /// <summary>The rectangle being dragged right now — dashed, so it reads as in-progress rather
    /// than as something already recorded.</summary>
    private static void DrawDrag(SKCanvas canvas, int width, int height, OverlayState state)
    {
        var x0 = (float)(Math.Min(state.DragX, state.DragX + state.DragW) * width);
        var y0 = (float)(Math.Min(state.DragY, state.DragY + state.DragH) * height);
        var x1 = (float)(Math.Max(state.DragX, state.DragX + state.DragW) * width);
        var y1 = (float)(Math.Max(state.DragY, state.DragY + state.DragH) * height);
        var rect = new SKRect(x0, y0, x1, y1);

        using var shade = new SKPaint { Color = new SKColor(0, 0, 0, 60) };
        canvas.DrawRect(rect, shade);

        using var halo = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 140),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(4, width / 300f),
            IsAntialias = true,
        };
        canvas.DrawRect(rect, halo);

        using var dash = SKPathEffect.CreateDash([Math.Max(6, width / 120f), Math.Max(4, width / 180f)], 0);
        using var stroke = new SKPaint
        {
            Color = Drawing,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2, width / 640f),
            PathEffect = dash,
            IsAntialias = true,
        };
        canvas.DrawRect(rect, stroke);

        // The size in the frame's own pixels, which is the number the agent will be given.
        var w = (int)Math.Round(Math.Abs(state.DragW) * width);
        var h = (int)Math.Round(Math.Abs(state.DragH) * height);
        DrawLabel(canvas, width, rect, $"{w} x {h}", Drawing, 255);
    }
}

/// <summary>Everything the overlay needs, snapshotted so the render thread never reads the model
/// while the UI thread is writing it.</summary>
public sealed record OverlayState(
    double Time,
    IReadOnlyList<OverlayMark> Marks,
    bool Dragging,
    double DragX,
    double DragY,
    double DragW,
    double DragH);

/// <summary>One annotation, and whether it is the one being worked on.</summary>
public sealed record OverlayMark(Annotation Annotation, bool Selected);
