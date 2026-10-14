# WiiRemoteMouse

A Windows application that reads a Nintendo Wii Remote & Nunchuk over Bluetooth and uses its inputs to control the mouse and keyboard. Built in C# (.NET).

## Status

- Pairing and connection to Windows: in-application pairing is in progress; currently requires pairing via Windows Bluetooth (ONLY IF YOU DONT WANT TO MESS WITH THE EMULATOR) or connecting via Dolphin emulator (RECOMMENDED) first
- Buttons (A, B, 1, 2, +, -, Home, D-pad, C, Z): reading correctly through WiimoteLib
- Nunchuk analog stick: reading smoothly with analog deflection and dead-zone filtering
- Mouse control & key mapping: fully working via Windows `SendInput` (directional cursor movement, mouse clicks, and full keyboard keys) with real-time sensitivity slider (1–10) and persistent configuration
- MotionPlus / gyro: not working yet. The extension reports `Ext:False`, even though the MotionPlus is a genuine RVL-026 unit. Next step is to diagnose the attachment

## Setup

Requirements:
- Windows 10/11 with Bluetooth
- .NET 10 SDK (`dotnet --version` should print 10.x)
- A Wii Remote (RVL-CNT-01) and optional Nunchuk

Steps:
1. Connect the remote:
   - *Recommended:* Connect via the **Dolphin emulator** (Controllers > Emulate the Wii's Bluetooth adapter / Real Wiimote).
   - *Alternative:* Pair in Windows Settings > Bluetooth & devices > Add device > Bluetooth (press the sync button in the battery compartment, select **Nintendo RVL-CNT-01**).
2. Clone the repo and open it in VS Code / terminal.
3. Run: `dotnet run`
4. Map buttons, analog stick directions, or mouse movement in the right panel and switch to your game/app to use it. U can also use it on Desktop, or any app for that matter, but it's meant to be used with games, not that I'm saying you can't use it with other apps.

## Notes and troubleshooting

- **Hot-Pluggable Nunchuk:** The Nunchuk does not need to be connected before launching the application. You can plug it into the Wii Remote at any time while the app is running, and it will be detected and ready to use immediately.
- **Connection / Pairing Workaround:** Native in-app Bluetooth connection without external setup is still an open goal and not yet solved. For the most reliable connection, pair and connect your Wii Remote using the **Dolphin emulator** before launching this app.
- **Raw HID approach was abandoned.** Hand-written `0x16`/`0x17` register reads never returned valid extension IDs. WiimoteLib handles the handshake instead.
- **LEDs flashing:** the sync button (or a lost link) puts the remote into discoverable mode. Don't press sync during normal use.
- **Remote doesn't reconnect after Bluetooth is toggled:** press a button first. If Settings shows "Driver error," remove the device, restart the Bluetooth service (`Restart-Service bthserv`), and pair again.
- **Disable Bluetooth power saving** on the adapter in Device Manager (Power Management tab) to reduce drops.

## Roadmap

1. Implement direct in-application Bluetooth connection/pairing (eliminating the Dolphin workaround)
2. Diagnose MotionPlus detection (`Ext:False`) and read gyro/tilt values
3. Make the tilt and gyro movements work properly
4. Profile management (save/load multiple mapping presets for different games)
