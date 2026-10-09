# WiiRemoteMouse

A Windows experiment that reads a Nintendo Wii Remote over Bluetooth and uses its inputs to drive the mouse. Built in C# (.NET) with VS Code.

## Status

- Pairing and connection to Windows: working (once paired, the remote reconnects after a button press)
- Buttons (A, B, 1, 2, +, -, Home, D-pad): reading correctly through WiimoteLib
- Accelerometer (X, Y, Z in g): streaming, Z reads about 1.00 at rest
- MotionPlus / gyro: not working yet. The extension reports `Ext:False`, even though the MotionPlus is a genuine RVL-026 unit. Next step is to diagnose the attachment or try a known-good accessory
- Mouse control (SendInput): not started yet

## Setup

Requirements:
- Windows 10/11 with Bluetooth
- .NET 10 SDK (`dotnet --version` should print 10.x)
- A Wii Remote (RVL-CNT-01) and, optionally, a Wii MotionPlus (RVL-026)

Steps:
1. Pair the remote in Settings > Bluetooth & devices > Add device > Bluetooth. Press the sync button inside the battery compartment, then select **Nintendo RVL-CNT-01**.
2. Clone the repo and open it in VS Code.
3. Add the library: `dotnet add package WiimoteLib`
4. Run: `dotnet run`

## Notes and troubleshooting

- **Raw HID approach was abandoned.** Hand-written `0x16`/`0x17` register reads never returned valid extension IDs. WiimoteLib handles the handshake instead.
- **LEDs flashing:** the sync button (or a lost link) puts the remote into discoverable mode. Don't press sync during normal use. Wake the remote with any button.
- **Remote doesn't reconnect after Bluetooth is toggled:** press a button first. If Settings shows "Driver error," remove the device, restart the Bluetooth service (`Restart-Service bthserv`), and pair again.
- **Disable Bluetooth power saving** on the adapter in Device Manager (Power Management tab) to reduce drops.

## Roadmap

1. Diagnose MotionPlus detection (`Ext:False`)
2. Read gyro values once the extension is detected
3. Map tilt and gyro to cursor movement, with a deadzone and calibration
4. Map A/B to left/right clicks using `SendInput`
5. Map other buttons as needed