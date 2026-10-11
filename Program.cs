using System.Drawing.Drawing2D;
using System.Globalization;
using WiimoteLib;
using PointF = System.Drawing.PointF;

ApplicationConfiguration.Initialize();

var remotePath = Path.Combine(AppContext.BaseDirectory, "remote.png");
var bPath = Path.Combine(AppContext.BaseDirectory, "bbutton.png");
if (!File.Exists(remotePath) || !File.Exists(bPath))
{
    MessageBox.Show("remote.png or bbutton.png not found. Put both in the project folder next to the .csproj.");
    return;
}

Application.Run(new MainForm(remotePath, bPath));

class MainForm : Form
{
    record Hot(int Bit, float X, float Y, float W, float H, bool Round);

    // Positions are fractions of the image size: X, Y = top-left corner, W, H = size.
    static readonly Hot[] Hots =
    {
        new(128,  0.4118f, 0.1099f, 0.1667f, 0.0387f, false), // Up
        new(256,  0.4118f, 0.1885f, 0.1667f, 0.0412f, false), // Down
        new(512,  0.2549f, 0.1486f, 0.1569f, 0.0400f, false), // Left
        new(1024, 0.5784f, 0.1486f, 0.1569f, 0.0400f, false), // Right
        new(1,    0.3529f, 0.2784f, 0.2941f, 0.0737f, true),  // A
        new(32,   0.1569f, 0.4557f, 0.1618f, 0.0424f, true),  // Minus
        new(64,   0.4118f, 0.4557f, 0.1667f, 0.0424f, true),  // Home
        new(16,   0.6814f, 0.4557f, 0.1618f, 0.0424f, true),  // Plus
        new(4,    0.3922f, 0.7016f, 0.2157f, 0.0549f, true),  // 1
        new(8,    0.3922f, 0.7890f, 0.2157f, 0.0549f, true),  // 2
    };

    // Set to true to show green outlines and click two corners of a button
    // to copy new position numbers to the clipboard.
    bool calibrate = false;

    readonly Image img;
    readonly Image bImg;
    readonly float drawW, drawH;
    Wiimote? wm;
    int mask;
    string status = "Connecting...";
    PointF? firstClick;

    static readonly StringFormat Center = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center
    };

    public MainForm(string remotePath, string bPath)
    {
        img = Image.FromFile(remotePath);
        bImg = Image.FromFile(bPath);
        float scale = 700f / img.Height;
        drawW = img.Width * scale;
        drawH = 700f;

        Text = "Wii Remote Input";
        ClientSize = new Size((int)drawW + 110, 735);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(40, 40, 45);

        Shown += (_, _) => ConnectRemote();
        FormClosed += (_, _) => { try { wm?.Disconnect(); } catch { } };
    }

    void ConnectRemote()
    {
        try
        {
            wm = new Wiimote();
            wm.WiimoteChanged += OnChanged;
            wm.Connect();
            wm.SetReportType(InputReport.ButtonsAccel, true);
            status = "Connected";
        }
        catch (Exception ex)
        {
            status = "Not connected: " + ex.Message;
        }
        Invalidate();
    }

    // Runs on a background thread, so only store state and ask the UI to repaint.
    void OnChanged(object? sender, WiimoteChangedEventArgs e)
    {
        var s = e.WiimoteState.ButtonState;
        int m = (s.A ? 1 : 0) | (s.B ? 2 : 0) | (s.One ? 4 : 0) | (s.Two ? 8 : 0)
              | (s.Plus ? 16 : 0) | (s.Minus ? 32 : 0) | (s.Home ? 64 : 0)
              | (s.Up ? 128 : 0) | (s.Down ? 256 : 0) | (s.Left ? 512 : 0) | (s.Right ? 1024 : 0);

        if (m == mask) return;
        mask = m;

        if (IsHandleCreated)
            BeginInvoke(new Action(() => Invalidate()));
    }

    // Calibration: click the top-left then bottom-right corner of a button.
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!calibrate) return;

        var p = new PointF(e.X / drawW, e.Y / drawH);
        if (firstClick is null)
        {
            firstClick = p;
            Text = "Now click the bottom-right corner";
            return;
        }

        var a = firstClick.Value;
        firstClick = null;
        string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture) + "f";
        string nums = $"{F(MathF.Min(a.X, p.X))}, {F(MathF.Min(a.Y, p.Y))}, " +
                      $"{F(MathF.Abs(p.X - a.X))}, {F(MathF.Abs(p.Y - a.Y))}";
        Clipboard.SetText(nums);
        Text = "Copied: " + nums;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        g.DrawImage(img, 0, 0, drawW, drawH);

        using var glow = new SolidBrush(Color.FromArgb(150, 255, 90, 0));
        foreach (var h in Hots)
        {
            var r = new RectangleF(h.X * drawW, h.Y * drawH, h.W * drawW, h.H * drawH);

            if ((mask & h.Bit) != 0)
            {
                if (h.Round) g.FillEllipse(glow, r); else g.FillRectangle(glow, r);
            }

            if (calibrate)
            {
                if (h.Round) g.DrawEllipse(Pens.Lime, r);
                else g.DrawRectangle(Pens.Lime, r.X, r.Y, r.Width, r.Height);
            }
        }

        // B trigger: the real button photo beside the remote, glowing while pressed.
        float bW = 80f;
        float bH = bW * bImg.Height / bImg.Width;
        var bRect = new RectangleF(drawW + 15, 120, bW, bH);
        g.DrawImage(bImg, bRect);
        if ((mask & 2) != 0)
        {
            using var path = RoundedRect(Rectangle.Round(bRect), 10);
            g.FillPath(glow, path);
        }
        g.DrawString("B (trigger)", Font, Brushes.White,
            new RectangleF(bRect.X - 10, bRect.Bottom + 4, bW + 20, 20), Center);

        g.DrawString(status, Font, Brushes.White,
            new RectangleF(0, drawH + 5, ClientSize.Width, 25), Center);
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}