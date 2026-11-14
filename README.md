# WiiRemoteMouse

A modern Windows application that connects a Nintendo Wii Remote (with Nunchuk and MotionPlus) and maps its buttons, analog stick, accelerometer, and gyroscope to mouse cursor movement, mouse buttons, scroll wheel actions, and keyboard inputs.

Built with C# and .NET 10.

---

## Features

- **Direct Dolphin-Based Connection & Pairing:**
  - Integrated Dolphin Emulator connection logic directly in-app.
  - Automatic continuous Bluetooth discovery and authentication (just press **1 + 2** or **Sync**).
  - Native Windows overlapped HID communication without needing external tools.
- **Full Control & Button Mapping:**
  - **Core Remote Buttons:** A, B (trigger), 1, 2, Plus (+), Minus (-), Home, and D-Pad (Up, Down, Left, Right).
  - **Nunchuk Extension:** C button, Z button, and full 360° analog stick with smooth deflection and deadzone filtering.
  - **Visual Status & Feedback:** Real-time visual remote and Nunchuk interface with interactive button glow, analog stick indicator, and a glowing blue Player 1 LED when connected.
- **6DOF Motion Controls (Accelerometer & Gyroscope):**
  - Real-time display for 3-axis Accelerometer (g) and 3-axis MotionPlus Gyroscope (deg/s).
  - Idle zero-point baselines configured to eliminate drift at rest.
  - **Mappable Motion Axes:** Map gestures (tilt, swing, flick) to keyboard keys, mouse clicks, or cursor movements:
    - `Accel X Up/Down`, `Accel Y Up/Down`, `Accel Z Up/Down`
    - `Gyro Left`, `Gyro Right`, `Gyro Up`, `Gyro Down`, `Roll Left`, `Roll Right`
  - **Velocity-Proportional Dynamics:** Harder or faster swings move the mouse cursor or scroll the wheel proportionally faster.
- **Mouse & Keyboard Emulation:**
  - High-performance input injection using Windows `SendInput` (hardware scan codes for gaming compatibility).
  - Smooth cursor movement with adjustable sensitivity slider (1–10).
  - Mouse wheel scrolling support (`Mouse Scroll Up` / `Mouse Scroll Down`).
  - Output is safely paused while the config window is focused, automatically resuming when you switch to your game or app.
- **Persistent Configuration:**
  - Automatically saves all key/mouse assignments and sensitivity preferences across sessions in `%AppData%/WiiRemoteMouse/mapping.json`.

---

## Setup & Requirements

### Requirements:             
- Windows 10 or Windows 11 with Bluetooth support
- [.NET 10 SDK](https://dotnet.microsoft.com/) (`dotnet --version` should print `10.x`)
- Nintendo Wii Remote (standard or Wii Remote Plus)
- *(Optional)* Nunchuk extension & Wii MotionPlus accessory

### Running the Application:
1. Clone the repository and navigate into the folder:
   ```powershell
   git clone <repo-url>
   cd WiiRemoteMouse
   ```
2. Build and run:
   ```powershell
   dotnet run
   ```
3. **Connecting your Remote:**
   - Launch the app.
   - Spam or press **1 + 2** (or the **Sync** button in the battery compartment) on your Wii Remote.
   - The app will automatically discover, authenticate, and connect the remote.
   - Once connected, the Player 1 blue LED on the remote graphic will illuminate.

---

## Mapping Inputs

1. Open the application.
2. In the right-hand panel, use the dropdown menus to assign any Wii input (buttons, stick directions, or motion axes) to:
   - **Keyboard Keys:** Letters A–Z, Numbers 0–9, F1–F12, Space, Enter, Shift, Ctrl, Alt, Arrow keys, etc.
   - **Mouse Actions:** Mouse Left, Mouse Right, Mouse Middle, Mouse Move (Up/Down/Left/Right), Mouse Scroll (Up/Down).
3. Adjust the **Sensitivity** slider to your preferred mouse speed.
4. Switch to your game or desktop app to begin playing!

---

## Motion & Gyro Notes

- **Zero-Point Baselines:**
  - Accelerometer: Calibrated to neutral resting values ($X: -0.24g, Y: -0.22g, Z: 0.76g$).
  - Gyroscope (MotionPlus): Calibrated to neutral resting values ($\text{Yaw}: -15^\circ/\text{s}, \text{Roll}: -44^\circ/\text{s}, \text{Pitch}: -105^\circ/\text{s}$).
- **Deadzones:**
  - Accelerometer triggers require $> 0.35g$ beyond idle.
  - Gyroscope triggers require $> 30^\circ/\text{s}$ beyond idle.
- **Hot-Plugging:**
  - You can plug or unplug the Nunchuk at any time while the application is running.

---

## License

MIT License. Developed for gaming and accessibility on Windows.
