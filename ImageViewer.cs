using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Asker;

// Full-size view of a unit's image: wheel zooms around the cursor, drag pans, double-click or 0 resets to fit, Esc closes.
public class ImageViewer : Window
{
    const double MinZoom = 0.1, MaxZoom = 20;
    readonly ScaleTransform _scale = new();
    readonly TranslateTransform _move = new();
    readonly Image _image;
    Point? _dragFrom;

    public ImageViewer(BitmapSource img, Window owner)
    {
        Owner = owner;
        Topmost = true; // the result window is topmost too
        ShowInTaskbar = false;
        Title = $"Image  {img.PixelWidth} × {img.PixelHeight}";
        Icon = owner.Icon;
        SetResourceReference(BackgroundProperty, "Bg");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var work = SystemParameters.WorkArea;
        Width = Math.Clamp(img.Width + 16, 400, work.Width * 0.9);   // +16/+39: window frame and title bar
        Height = Math.Clamp(img.Height + 39, 300, work.Height * 0.9);

        _image = new Image
        {
            Source = img,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly, // fit big images, show small ones at 100%
            RenderTransform = new TransformGroup { Children = { _scale, _move } },
            Cursor = Cursors.SizeAll,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Content = new Grid { ClipToBounds = true, Background = Brushes.Transparent, Children = { _image } };

        MouseWheel += OnWheel;
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += (_, _) => { _dragFrom = null; ReleaseMouseCapture(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is Key.D0 or Key.NumPad0) Reset();
        };
        SourceInitialized += (_, _) => App.ApplyCaptureSetting(this);
    }

    void Reset()
    {
        _scale.ScaleX = _scale.ScaleY = 1;
        _move.X = _move.Y = 0;
    }

    // Keep the image point under the cursor fixed: it sits at p*s + t, so t' = t + p*(s - s').
    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var p = e.GetPosition(_image); // untransformed image coordinates
        double s = _scale.ScaleX, next = Math.Clamp(s * (e.Delta > 0 ? 1.2 : 1 / 1.2), MinZoom, MaxZoom);
        _move.X += p.X * (s - next);
        _move.Y += p.Y * (s - next);
        _scale.ScaleX = _scale.ScaleY = next;
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Reset(); return; }
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var now = e.GetPosition(this);
        _move.X += now.X - from.X;
        _move.Y += now.Y - from.Y;
        _dragFrom = now;
    }
}
