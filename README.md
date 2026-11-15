# WiiRemoteInput

A modern Windows application that connects a Nintendo Wii Remote (with Nunchuk and MotionPlus) and maps its buttons, analog stick, steering/tilt angles, accelerometer swings, and gyroscope to either **Keyboard & Mouse** or a virtual **Xbox 360 Gamepad**.

Built with C# and .NET 10.

---

## Features

- **Direct Dolphin-Based Connection & Pairing:**
  - Integrated Dolphin Emulator connection logic directly in-app.
  - Automatic continuous Bluetooth discovery and authentication (press **1 + 2** or **Sync**).
  - Native Windows overlapped HID communication without needing external tools.
- **Two Output Modes:**
  - **Keyboard & Mouse:** Hardware scan-code keyboard keys (`SendInput`), mouse buttons, continuous mouse movement, and mouse wheel scrolling with an adjustable **Sensitivity** slider (1–10).
  - **Gamepad (Xbox 360):** Virtual Xbox 360 controller emulation via `Nefarius.ViGEm.Client` (ViGEmBus), with linear analog steering (`-75°` to `+75°` → `LeftThumbX`), analog sticks, triggers (`LT`/`RT`), and all Xbox 360 buttons.
- **Categorized Bindings:**
  - **Wii Remote:** `A`, `B`, `1`, `2`, `Plus (+)`, `Minus (-)`, `Home`, `D-pad Up/Down/Left/Right`
  - **Nunchuck:** `C`, `Z`, `Stick Up/Down/Left/Right` (automatically grayed out when disconnected)
  - **Motion:**
    - **Angle:** `Steer Left/Right` (`-75°..+75°` with `±5°` deadzone), `Pitch Up/Down`, `Roll Left/Right`
    - **Accel:** `Swing Right/Left`, `Swing Backward/Forward`, `Swing Up/Down` (high-pass filtered linear acceleration with `0.55g` threshold and 220 ms rebound lockout)
    - **Gyro:** `Gyro Left/Right/Up/Down` (automatically grayed out when MotionPlus is inactive)
- **Steering 0° Calibration & Visual Feedback:**
  - **Calibrate Steer 0°** button to set your neutral steering-wheel angle on any Wii Remote.
  - **Clear All Bindings** button to reset the active mode's bindings.
  - Real-time visual Wii Remote & Nunchuk overlay with button highlights, Player 1 blue LED, and a 3-column **Angles / Accel / Gyro** readout card.
- **Persistent Configuration:**
  - Saves Keyboard & Mouse bindings, Gamepad bindings, and sensitivity in `%AppData%/WiiRemoteInput/mapping.json`.

---

## Setup & Requirements

### Requirements
- Windows 10 or Windows 11 with Bluetooth support
- [.NET 10 SDK](https://dotnet.microsoft.com/) (or .NET 10 Desktop Runtime for running the built `.exe`)
- *(For Gamepad mode)* [ViGEmBus Driver](https://github.com/nefarius/ViGEmBus/releases)
- Nintendo Wii Remote (standard or Wii Remote Plus), plus optional Nunchuk / MotionPlus

### Installing ViGEmBus Driver (Required for Gamepad Mode)
If you want to use the virtual **Xbox 360 Gamepad** mode, you must install the ViGEmBus kernel driver:
1. Go to the [ViGEmBus Releases page](https://github.com/nefarius/ViGEmBus/releases).
2. Download the latest `ViGEmBus_Setup_x64.msi` (or `ViGEmBusSetup_x64.exe`) installer.
3. Run the installer and follow the on-screen prompts (accept the license agreement and click **Install**).
4. Restart your PC if prompted, then launch `WiiRemoteInput.exe`.

### Building & Running
```powershell
dotnet build -c Release
```
The release executable is output to `bin\Release\net10.0-windows\WiiRemoteInput.exe`.

To publish a standalone single-file `.exe` (no .NET runtime required):
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

---

## License

MIT License.
