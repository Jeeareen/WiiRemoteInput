using System;
using System.Threading;
using WiimoteLib;

class Program
{
    static int count = 0;

    static void Main()
    {
        var wm = new Wiimote();
        wm.WiimoteChanged += OnChanged;
        wm.Connect();

        wm.SetReportType(InputReport.ButtonsAccel, true);
        Console.WriteLine("Connected. Press A/B, tilt the remote. Ctrl+C to quit.");

        Thread.Sleep(Timeout.Infinite);
    }

static void OnChanged(object sender, WiimoteChangedEventArgs e)
{
    var b = e.WiimoteState.ButtonState;

    if (b.A) Console.WriteLine("A pressed");
    if (b.B) Console.WriteLine("B pressed");
    if (b.One) Console.WriteLine("1 pressed");
    if (b.Two) Console.WriteLine("2 pressed");
    if (b.Plus) Console.WriteLine("+ pressed");
    if (b.Minus) Console.WriteLine("- pressed");
    if (b.Home) Console.WriteLine("Home pressed");
    if (b.Up) Console.WriteLine("Up pressed");
    if (b.Down) Console.WriteLine("Down pressed");
    if (b.Left) Console.WriteLine("Left pressed");
    if (b.Right) Console.WriteLine("Right pressed");
}
}