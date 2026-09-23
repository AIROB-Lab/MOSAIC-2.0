using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MOSAIC.Visualization.Ultrasound;

/// <summary>
/// Lightweight Avalonia control specialised for displaying live ultrasound B-mode frames.
/// </summary>
/// <remarks>
/// <para>
/// Backed by an Avalonia <see cref="WriteableBitmap"/> updated in-place from
/// <see cref="PushFrame"/>. Rendering uses the standard
/// <see cref="DrawingContext.DrawImage(IImage, Rect, Rect)"/> path, which means
/// Avalonia handles HiDPI scaling, coordinate transforms, and clipping correctly
/// without any custom Skia plumbing.
/// </para>
/// <para>
/// <b>Aspect ratio.</b> The frame is drawn with <see cref="Stretch.Uniform"/> semantics:
/// scaled up or down to fit the control's bounds while preserving the source aspect ratio,
/// centred within the bounds. Letterbox / pillarbox black bars appear on whichever axis
/// isn't the limiting one.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="PushFrame"/> may be called from any thread. The frame
/// upload is marshalled to the UI thread (Avalonia bitmap APIs are not thread-safe) and
/// the visual is then invalidated; rendering happens on the next render tick.
/// </para>
/// <para>
/// <b>Buffer format.</b> Source bytes must be 32-bpp top-down BGRA — exactly what
/// <c>GetDIBits</c> with a negative <c>biHeight</c> produces. No row reversal or
/// channel reordering is performed.
/// </para>
/// </remarks>
public sealed class UltrasoundView : Control
{
    private WriteableBitmap? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;

    static UltrasoundView()
    {
        AffectsRender<UltrasoundView>();
    }

    /// <summary>
    /// Pushes a new frame into the view. The buffer is copied — the caller may free or
    /// reuse it immediately after this call returns.
    /// </summary>
    public void PushFrame(byte[] bgra, int width, int height)
    {
        if (bgra is null || width <= 0 || height <= 0) return;
        var expected = width * height * 4;
        if (bgra.Length < expected) return;

        if (Dispatcher.UIThread.CheckAccess())
            UploadAndInvalidate(bgra, width, height);
        else
            Dispatcher.UIThread.Post(() => UploadAndInvalidate(bgra, width, height),
                                     DispatcherPriority.Render);
    }

    private void UploadAndInvalidate(byte[] bgra, int width, int height)
    {
        if (_bitmap is null || _bitmapWidth != width || _bitmapHeight != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
            _bitmapWidth  = width;
            _bitmapHeight = height;
        }

        using (var fb = _bitmap.Lock())
        {
            // Source rows are tightly packed (width × 4 bytes). Destination rows may
            // have stride padding, so copy row by row to honour fb.RowBytes.
            int srcStride = width * 4;
            for (int y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    bgra, y * srcStride,
                    fb.Address + y * fb.RowBytes,
                    srcStride);
            }
        }

        InvalidateVisual();
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 1 || bounds.Height < 1) return;

        // Black background — matches the ultrasound's natural surround.
        context.FillRectangle(Brushes.Black, bounds);

        if (_bitmap is null) return;

        // Stretch.Uniform — preserve aspect, centre in bounds.
        double srcAR = _bitmapWidth / (double)_bitmapHeight;
        double dstAR = bounds.Width / bounds.Height;

        double dstW, dstH;
        if (srcAR > dstAR)
        {
            dstW = bounds.Width;
            dstH = bounds.Width / srcAR;
        }
        else
        {
            dstH = bounds.Height;
            dstW = bounds.Height * srcAR;
        }
        var dstX = (bounds.Width  - dstW) / 2.0;
        var dstY = (bounds.Height - dstH) / 2.0;

        var srcRect = new Rect(0, 0, _bitmapWidth, _bitmapHeight);
        var dstRect = new Rect(dstX, dstY, dstW, dstH);
        context.DrawImage(_bitmap, srcRect, dstRect);
    }

    /// <summary>
    /// Claim all the space our parent gives us. Without this override,
    /// <see cref="Avalonia.Controls.Control"/>'s default measurement returns
    /// a zero size — the control is laid out at zero pixels and
    /// <see cref="Render"/> bails on the <c>bounds.Width &lt; 1</c> check, leaving
    /// nothing visible despite the bitmap being correctly populated.
    /// </summary>
    /// <remarks>
    /// If both dimensions of <paramref name="availableSize"/> are finite (the
    /// typical case — we live in a Grid column with a finite width / a Border
    /// with a MinHeight), we fill it. If a dimension is infinite (e.g. inside
    /// an unbounded ScrollViewer), we fall back to the source frame's natural
    /// size so the control still has a sensible footprint to render into.
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize)
    {
        double fallbackW = _bitmapWidth  > 0 ? _bitmapWidth  : 320;
        double fallbackH = _bitmapHeight > 0 ? _bitmapHeight : 240;
        double w = double.IsInfinity(availableSize.Width)  ? fallbackW : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? fallbackH : availableSize.Height;
        return new Size(w, h);
    }

    /// <summary>Disposes the internal bitmap. Safe to call multiple times.</summary>
    public void Reset()
    {
        if (Dispatcher.UIThread.CheckAccess()) DoReset();
        else Dispatcher.UIThread.Post(DoReset);
    }

    private void DoReset()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _bitmapWidth = 0;
        _bitmapHeight = 0;
        InvalidateVisual();
    }
}