using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using WiimoteLib;
using PointF = System.Drawing.PointF;

ApplicationConfiguration.Initialize();

string Img(string name) => Path.Combine(AppContext.BaseDirectory, name);
string[] needed = { "remote.png", "bbutton.png", "nunchuk.png", "nunchuk_c.png", "nunchuk_z.png" };
var missing = needed.Where(n => !File.Exists(Img(n))).ToArray();
if (missing.Length > 0)
{
    MessageBox.Show("Missing image(s): " + string.Join(", ", missing) +
                    "\nPut them in the project folder next to the .csproj.");
    return;
}

Application.Run(new MainForm(Img("remote.png"), Img("bbutton.png"),
                             Img("nunchuk.png"), Img("nunchuk_c.png"), Img("nunchuk_z.png")));

enum OutputType { Keyboard, MouseButton, MouseMove }
enum MouseMoveDir { Up, Down, Left, Right }

// A keyboard key (by scan code), a mouse button, or a directional mouse cursor movement.
record Output(string Name, OutputType Type, ushort Code, bool Extended = false, MouseMoveDir MoveDir = MouseMoveDir.Up);

class MainForm : Form
{
    record Hot(int Bit, float X, float Y, float W, float H, bool Round);
    record Source(string Name, int Bit);

    // Positions are fractions of the remote image size: X, Y = top-left corner, W, H = size.
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

    // Bit flags for the extra inputs.
    const int NunchukC = 2048;
    const int NunchukZ = 4096;
    const int StickUp = 8192;
    const int StickDown = 16384;
    const int StickLeft = 32768;
    const int StickRight = 65536;

    // Every Wii input that can be assigned, in the order shown in the panel.
    static readonly Source[] Sources =
    {
        new("C", NunchukC), new("Z", NunchukZ),
        new("A", 1), new("B", 2), new("1", 4), new("2", 8),
        new("Plus (+)", 16), new("Minus (-)", 32), new("Home", 64),
        new("D-pad Up", 128), new("D-pad Down", 256), new("D-pad Left", 512), new("D-pad Right", 1024),
        new("Stick Up", StickUp), new("Stick Down", StickDown),
        new("Stick Left", StickLeft), new("Stick Right", StickRight),
    };

    static readonly Output[] Outputs = BuildOutputs();

    static Output[] BuildOutputs()
    {
        var l = new List<Output>
        {
            new("Mouse Move Up", OutputType.MouseMove, 0, MoveDir: MouseMoveDir.Up),
            new("Mouse Move Down", OutputType.MouseMove, 0, MoveDir: MouseMoveDir.Down),
            new("Mouse Move Left", OutputType.MouseMove, 0, MoveDir: MouseMoveDir.Left),
            new("Mouse Move Right", OutputType.MouseMove, 0, MoveDir: MouseMoveDir.Right),
            new("Mouse Left", OutputType.MouseButton, 0x0002),
            new("Mouse Right", OutputType.MouseButton, 0x0008),
            new("Mouse Middle", OutputType.MouseButton, 0x0020),
        };
        void Add(string n, ushort sc, bool ext = false) => l.Add(new Output(n, OutputType.Keyboard, sc, ext));

        // Letters A-Z (hardware scan codes)
        ushort[] letters =
        {
            0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
            0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C
        };
        for (int i = 0; i < 26; i++) Add(((char)('A' + i)).ToString(), letters[i]);

        // Digits 0-9
        ushort[] digits = { 0x0B, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A };
        for (int i = 0; i < 10; i++) Add(i.ToString(), digits[i]);

        // F1-F12
        for (int i = 1; i <= 12; i++)
            Add("F" + i, i <= 10 ? (ushort)(0x3A + i) : (ushort)(0x57 + (i - 11)));

        Add("Space", 0x39);
        Add("Enter", 0x1C);
        Add("Esc", 0x01);
        Add("Tab", 0x0F);
        Add("Backspace", 0x0E);
        Add("Left Shift", 0x2A);
        Add("Right Shift", 0x36);
        Add("Left Ctrl", 0x1D);
        Add("Left Alt", 0x38);
        Add("Up Arrow", 0x48, true);
        Add("Down Arrow", 0x50, true);
        Add("Left Arrow", 0x4B, true);
        Add("Right Arrow", 0x4D, true);
        return l.ToArray();
    }

    const float LeftW = 190f;    // left panel (Nunchuk)
    const int PanelW = 455;      // right panel (assignments) (total width 890)

    static readonly string MapPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WiiRemoteMouse", "mapping.json");

    // Set to true to show green outlines and click two corners of a button
    // on the remote to copy new position numbers to the clipboard.
    bool calibrate = false;

    readonly Image img, bImg, nImg, cImg, zImg;
    readonly float drawW, drawH;
    readonly RectangleF cRect, zRect, nRect;
    readonly Output?[] assigned = new Output?[Sources.Length];
    readonly Label outLabel;
    readonly System.Windows.Forms.Timer moveTimer;
    Wiimote? wm;
    int mask;
    float stickX, stickY;
    float mouseSpeed = 17.5f;
    int currentSensitivity = 5;
    float mouseRemainderX = 0f;
    float mouseRemainderY = 0f;
    const float topPad = 45f;
    volatile bool appActive;   // true while this window is in front
    string status = "Connecting...";
    PointF? firstClick;

    static readonly Brush Idle = new SolidBrush(Color.FromArgb(200, 200, 205));
    static readonly Brush PressedBrush = new SolidBrush(Color.OrangeRed);
    static readonly Brush DotIdle = new SolidBrush(Color.FromArgb(120, 120, 120, 130));
    static readonly StringFormat Center = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center
    };

    public MainForm(string remotePath, string bPath, string nunchukPath, string cPath, string zPath)
    {
        img = Image.FromFile(remotePath);
        bImg = Image.FromFile(bPath);
        nImg = Image.FromFile(nunchukPath);
        cImg = Image.FromFile(cPath);
        zImg = Image.FromFile(zPath);

        float scale = 700f / img.Height;
        drawW = img.Width * scale;
        drawH = 700f;

        // Status is on top, followed by padding.
        float cW = 56f, cH = cW * cImg.Height / cImg.Width;
        float zW = 70f, zH = zW * zImg.Height / zImg.Width;
        float nH = 520f, nW = nH * nImg.Width / nImg.Height;
        cRect = new RectangleF(LeftW / 2 - cW / 2, topPad, cW, cH);
        zRect = new RectangleF(LeftW / 2 - zW / 2, cRect.Bottom + 12, zW, zH);
        nRect = new RectangleF(LeftW / 2 - nW / 2, zRect.Bottom + 20, nW, nH);

        Text = "Wii Remote Input";
        ClientSize = new Size(895, 800);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(40, 40, 45);

        // ---- Right panel: one dropdown per Wii input ----
        var saved = LoadMapping();
        var panel = new Panel
        {
            Left = (int)(LeftW + drawW + 110),
            Top = 0,
            Width = PanelW,
            Height = 800,
            BackColor = Color.FromArgb(50, 50, 56)
        };
        Controls.Add(panel);

        panel.Controls.Add(new Label
        {
            Text = "Assign keys / mouse buttons and movement",
            ForeColor = Color.White,
            Font = new Font(Font, FontStyle.Bold),
            Left = 12,
            Top = 14,
            Width = PanelW - 24,
            Height = 24
        });

        // Mouse Sensitivity: "Sensitivity", value label (1-10), and slider (moved down by 20px)
        var speedLbl = new Label
        {
            Text = "Sensitivity",
            ForeColor = Color.White,
            Left = 12,
            Top = 70,
            AutoSize = true
        };
        var valLbl = new Label
        {
            Text = "5",
            ForeColor = Color.White,
            Font = new Font(Font, FontStyle.Bold),
            Left = 115,
            Top = 70,
            AutoSize = true
        };
        var speedTrack = new TrackBar
        {
            Left = 160,
            Top = 66,
            Width = 240,
            Height = 30,
            AutoSize = false,
            TickStyle = TickStyle.None,
            Minimum = 1,
            Maximum = 10,
            Value = 5
        };
        speedTrack.ValueChanged += (_, _) =>
        {
            currentSensitivity = speedTrack.Value;
            valLbl.Text = currentSensitivity.ToString();
            mouseSpeed = currentSensitivity * 3.5f;
            SaveMapping();
        };
        panel.Controls.Add(speedLbl);
        panel.Controls.Add(valLbl);
        panel.Controls.Add(speedTrack);

        var names = new[] { "(none)" }.Concat(Outputs.Select(o => o.Name)).ToArray();
        for (int i = 0; i < Sources.Length; i++)
        {
            int row = i;
            var lbl = new Label
            {
                Text = Sources[i].Name,
                ForeColor = Color.White,
                Left = 12,
                Top = 130 + i * 31 + 4,
                Width = 145,
                Height = 24
            };
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = 160,
                Top = 130 + i * 31,
                Width = 240,
                MaxDropDownItems = 16
            };
            cb.Items.AddRange(names);

            int sel = 0;
            if (saved.Mappings.TryGetValue(Sources[i].Name, out var savedName))
            {
                int idx = Array.FindIndex(Outputs, o => o.Name == savedName);
                if (idx >= 0) sel = idx + 1;
            }
            cb.SelectedIndex = sel;
            assigned[i] = sel == 0 ? null : Outputs[sel - 1];

            cb.SelectedIndexChanged += (_, _) => OnAssignChanged(row, cb.SelectedIndex);
            panel.Controls.Add(lbl);
            panel.Controls.Add(cb);
        }

        currentSensitivity = saved.Speed is >= 1 and <= 10 ? saved.Speed : (saved.Speed > 10 ? Math.Clamp((int)MathF.Round(saved.Speed / 3.5f), 1, 10) : 5);
        speedTrack.Value = currentSensitivity;
        valLbl.Text = currentSensitivity.ToString();
        mouseSpeed = currentSensitivity * 3.5f;

        outLabel = new Label
        {
            ForeColor = Color.Gainsboro,
            Left = 12,
            Top = 718,
            Width = PanelW - 24,
            Height = 55,
            Text = "Output paused: this window is active.\nSwitch to your game to send keys."
        };
        panel.Controls.Add(outLabel);

        // Timer for smooth cursor movement (approx 80-100 fps ticks)
        moveTimer = new System.Windows.Forms.Timer { Interval = 12 };
        moveTimer.Tick += OnMoveTimerTick;
        moveTimer.Start();

        // Output is only sent while another window (the game) is in front, so clicking
        // around this window never types into itself.
        Activated += (_, _) =>
        {
            appActive = true;
            ReleaseAll();
            outLabel.Text = "Output paused: this window is active.\nSwitch to your game to send keys.";
        };
        Deactivate += (_, _) =>
        {
            appActive = false;
            outLabel.Text = "Output active.";
        };

        Shown += (_, _) => ConnectRemote();
        FormClosed += (_, _) =>
        {
            ReleaseAll();
            try { wm?.Disconnect(); } catch { }
        };
    }

    record AppConfig(Dictionary<string, string> Mappings, int Speed = 14);

    void OnMoveTimerTick(object? sender, EventArgs e)
    {
        if (appActive) return;

        float vx = 0f;
        float vy = 0f;

        for (int i = 0; i < Sources.Length; i++)
        {
            var o = assigned[i];
            if (o is null || o.Type != OutputType.MouseMove) continue;

            int bit = Sources[i].Bit;
            bool active = (mask & bit) != 0;

            // Determine intensity: if it's stick input, use analog magnitude if deflected; otherwise 1.0f
            float intensity = 0f;
            if (bit == StickUp && stickY > 0.08f) intensity = stickY;
            else if (bit == StickDown && stickY < -0.08f) intensity = -stickY;
            else if (bit == StickLeft && stickX < -0.08f) intensity = -stickX;
            else if (bit == StickRight && stickX > 0.08f) intensity = stickX;
            else if (active) intensity = 1.0f;

            if (intensity <= 0.01f) continue;

            switch (o.MoveDir)
            {
                case MouseMoveDir.Up:
                    vy -= intensity;
                    break;
                case MouseMoveDir.Down:
                    vy += intensity;
                    break;
                case MouseMoveDir.Left:
                    vx -= intensity;
                    break;
                case MouseMoveDir.Right:
                    vx += intensity;
                    break;
            }
        }

        if (vx == 0f && vy == 0f)
        {
            mouseRemainderX = 0f;
            mouseRemainderY = 0f;
            return;
        }

        // Clamp vector magnitude if exceeding 1.0
        float mag = MathF.Sqrt(vx * vx + vy * vy);
        if (mag > 1.0f)
        {
            vx /= mag;
            vy /= mag;
        }

        float targetDx = vx * mouseSpeed + mouseRemainderX;
        float targetDy = vy * mouseSpeed + mouseRemainderY;

        int stepX = (int)MathF.Round(targetDx);
        int stepY = (int)MathF.Round(targetDy);

        mouseRemainderX = targetDx - stepX;
        mouseRemainderY = targetDy - stepY;

        if (stepX != 0 || stepY != 0)
        {
            InputOut.SendMouseMove(stepX, stepY);
        }
    }

    void OnAssignChanged(int i, int comboIndex)
    {
        var old = assigned[i];
        if (old is not null && old.Type != OutputType.MouseMove)
            InputOut.Send(old, false);   // don't leave the old output stuck down
        assigned[i] = comboIndex <= 0 ? null : Outputs[comboIndex - 1];
        SaveMapping();
    }

    void ReleaseAll()
    {
        foreach (var o in assigned)
            if (o is not null && o.Type != OutputType.MouseMove)
                InputOut.Send(o, false);
    }

    static AppConfig LoadMapping()
    {
        try
        {
            if (File.Exists(MapPath))
            {
                var json = File.ReadAllText(MapPath);
                // Try deserializing full AppConfig
                try
                {
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config?.Mappings is not null) return config;
                }
                catch { }

                // Fallback to legacy dictionary format
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict is not null) return new AppConfig(dict);
            }
        }
        catch { }
        return new AppConfig(new Dictionary<string, string>());
    }

    void SaveMapping()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MapPath)!);
            var d = new Dictionary<string, string>();
            for (int i = 0; i < Sources.Length; i++)
                if (assigned[i] is not null) d[Sources[i].Name] = assigned[i]!.Name;
            var config = new AppConfig(d, currentSensitivity);
            File.WriteAllText(MapPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    void ConnectRemote()
    {
        try
        {
            wm = new Wiimote();
            wm.WiimoteChanged += OnChanged;
            wm.Connect();
            // Report mode with buttons + accelerometer + extension data (needed for the Nunchuk).
            // If this name is underlined, type "InputReport." and pick the one with Extension and Accel.
            wm.SetReportType(InputReport.ExtensionAccel, true);
            status = "Connected";
        }
        catch (Exception ex)
        {
            status = "Not connected: " + ex.Message;
        }
        Invalidate();
    }

    // Runs on a background thread, so only store state, send input, and ask the UI to repaint.
    void OnChanged(object? sender, WiimoteChangedEventArgs e)
    {
        var s = e.WiimoteState.ButtonState;
        var n = e.WiimoteState.NunchukState;

        // Stick position, with a small dead zone so it doesn't jitter at rest.
        float jx = Math.Clamp(n.Joystick.X, -1f, 1f);
        float jy = Math.Clamp(n.Joystick.Y, -1f, 1f);
        if (MathF.Abs(jx) < 0.08f) jx = 0;
        if (MathF.Abs(jy) < 0.08f) jy = 0;

        const float T = 0.5f;   // how far the stick must be pushed to count as a direction press
        int m = (s.A ? 1 : 0) | (s.B ? 2 : 0) | (s.One ? 4 : 0) | (s.Two ? 8 : 0)
              | (s.Plus ? 16 : 0) | (s.Minus ? 32 : 0) | (s.Home ? 64 : 0)
              | (s.Up ? 128 : 0) | (s.Down ? 256 : 0) | (s.Left ? 512 : 0) | (s.Right ? 1024 : 0)
              | (n.C ? NunchukC : 0) | (n.Z ? NunchukZ : 0)
              | (jy > T ? StickUp : 0) | (jy < -T ? StickDown : 0)
              | (jx < -T ? StickLeft : 0) | (jx > T ? StickRight : 0);

        bool stickMoved = MathF.Abs(jx - stickX) > 0.02f || MathF.Abs(jy - stickY) > 0.02f;
        if (m == mask && !stickMoved) return;

        stickX = jx;
        stickY = jy;
        int old = mask;
        mask = m;

        // Send the assigned key / mouse button for every input that just changed.
        int changed = m ^ old;
        if (!appActive && changed != 0)
        {
            for (int i = 0; i < Sources.Length; i++)
            {
                if ((changed & Sources[i].Bit) != 0 && assigned[i] is { } o && o.Type != OutputType.MouseMove)
                    InputOut.Send(o, (m & Sources[i].Bit) != 0);
            }
        }

        if (IsHandleCreated)
            BeginInvoke(new Action(() => Invalidate()));
    }

    // Calibration: click the top-left then bottom-right corner of a button on the remote.
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!calibrate) return;

        var p = new PointF((e.X - LeftW) / drawW, e.Y / drawH);
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

        using var glow = new SolidBrush(Color.FromArgb(150, 255, 90, 0));

        // ---- Left panel: Nunchuk, with C and Z above it ----
        g.DrawImage(nImg, nRect);
        g.DrawImage(cImg, cRect);
        g.DrawImage(zImg, zRect);
        if ((mask & NunchukC) != 0) g.FillEllipse(glow, cRect);
        if ((mask & NunchukZ) != 0)
        {
            using var zp = RoundedRect(Rectangle.Round(zRect), 6);
            g.FillPath(glow, zp);
        }

        // Stick indicator: a dot that moves with the analog stick.
        float scx = nRect.X + 0.4975f * nRect.Width;
        float scy = nRect.Y + 0.21f * nRect.Height;
        float travel = 0.17f * nRect.Width;
        float dotR = 0.04f * nRect.Width;
        float dotX = scx + stickX * travel;
        float dotY = scy - stickY * travel;   // stick Y is positive when pushed up
        bool pushed = stickX != 0 || stickY != 0;
        g.FillEllipse(pushed ? PressedBrush : DotIdle,
                      dotX - dotR, dotY - dotR, dotR * 2, dotR * 2);

        // Status drawn on top of the left and middle areas
        g.DrawString(status, Font, Brushes.White,
            new RectangleF(0, 10, ClientSize.Width - PanelW, 25), Center);

        g.DrawString("Nunchuk", Font, Brushes.White,
            new RectangleF(0, nRect.Bottom + 4, LeftW, 20), Center);

        // ---- Middle: the remote and the B trigger ----
        g.TranslateTransform(LeftW, topPad);

        g.DrawImage(img, 0, 0, drawW, drawH);
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

        g.DrawString("Remote", Font, Brushes.White,
            new RectangleF(0, drawH + 4, drawW, 20), Center);

        g.ResetTransform();
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

static class InputOut
{
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);

    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;
    const uint MOUSEEVENTF_MOVE = 0x0001;

    public static void Send(Output o, bool down)
    {
        if (o.Type == OutputType.MouseButton) SendMouse(o.Code, down);
        else if (o.Type == OutputType.Keyboard) SendKey(o.Code, o.Extended, down);
    }

    public static void SendMouseMove(int dx, int dy)
    {
        var i = new INPUT { type = 0 };   // 0 = mouse
        i.u.mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE };
        SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
    }

    // Scan codes are used instead of virtual-key codes because many games read hardware scan codes.
    static void SendKey(ushort scan, bool extended, bool down)
    {
        var i = new INPUT { type = 1 };   // 1 = keyboard
        i.u.ki = new KEYBDINPUT
        {
            wScan = scan,
            dwFlags = KEYEVENTF_SCANCODE
                      | (extended ? KEYEVENTF_EXTENDEDKEY : 0u)
                      | (down ? 0u : KEYEVENTF_KEYUP)
        };
        SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
    }

    // downFlag is the MOUSEEVENTF_xxxDOWN value; the matching "up" flag is that value shifted left by 1.
    static void SendMouse(ushort downFlag, bool down)
    {
        var i = new INPUT { type = 0 };   // 0 = mouse
        i.u.mi = new MOUSEINPUT { dwFlags = down ? downFlag : (uint)(downFlag << 1) };
        SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
    }
}