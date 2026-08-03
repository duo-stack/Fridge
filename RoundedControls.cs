using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Fridge;

internal static class RoundedGeometry
{
    public static GraphicsPath CreatePath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
            path.CloseFigure();
            return path;
        }

        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class RoundedPanel : Panel
{
    private bool _isFocused;
    private bool _hasError;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 8;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.FromArgb(214, 220, 226);

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FocusBorderColor { get; set; } = Color.FromArgb(25, 103, 87);

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ErrorBorderColor { get; set; } = Color.FromArgb(174, 55, 55);

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int BorderThickness { get; set; } = 1;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (_isFocused == value) return;
            _isFocused = value;
            Invalidate();
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HasError
    {
        get => _hasError;
        set
        {
            if (_hasError == value) return;
            _hasError = value;
            Invalidate();
        }
    }

    public RoundedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        using var path = RoundedGeometry.CreatePath(new Rectangle(0, 0, Width, Height), CornerRadius);
        var oldRegion = Region;
        Region = new Region(path);
        oldRegion?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = HasError ? ErrorBorderColor : IsFocused ? FocusBorderColor : BorderColor;
        var thickness = HasError || IsFocused ? Math.Max(2, BorderThickness) : BorderThickness;
        var inset = thickness / 2F;
        var bounds = Rectangle.Round(new RectangleF(inset, inset, Width - thickness - 1F, Height - thickness - 1F));
        using var path = RoundedGeometry.CreatePath(bounds, CornerRadius);
        using var pen = new Pen(color, thickness);
        eventArgs.Graphics.DrawPath(pen, path);
    }
}

internal sealed class RoundedButton : Button
{
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 6;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FocusBorderColor { get; set; } = Color.FromArgb(25, 103, 87);

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        ResizeRedraw = true;
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        using var path = RoundedGeometry.CreatePath(new Rectangle(0, 0, Width, Height), CornerRadius);
        var oldRegion = Region;
        Region = new Region(path);
        oldRegion?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (!Focused || !ShowFocusCues) return;

        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(2, 2, Width - 5, Height - 5);
        using var path = RoundedGeometry.CreatePath(bounds, Math.Max(2, CornerRadius - 2));
        using var pen = new Pen(FocusBorderColor, 2);
        eventArgs.Graphics.DrawPath(pen, path);
    }
}
