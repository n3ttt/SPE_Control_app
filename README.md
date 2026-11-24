# SPE Control

A friendlier control panel for the SPE Expert 1.5K-FA linear amplifier, usable from any computer, phone or tablet on the home network.

It shows gauges, explains warnings in plain English, lets you select bands and power levels with one click, and keeps a 2-minute history graph and an event log.

## Installing (Windows)

1. Run **SPEControl-Setup.exe**.
   Windows may show "Windows protected your PC" because the app isn't code-signed. Click **More info**, then **Run anyway**.
2. Click through the installer. It starts the app at the end.
3. Plug the amplifier in by USB. The app finds it automatically.

A gauge icon appears by the clock in the taskbar's notification area.
- **Double-click it** to open the panel.
- **Right-click it** to see the address phones and tablets should use, choose the USB port by hand, or turn "start with Windows" on or off.

## Phones and tablets

Connect to the same Wi-Fi as the computer, open the browser and type the address shown, for example `http://192.168.1.188:8080`. "Add to Home Screen" makes it behave like an app. This works on iPad and iPhone too.

The app answers only devices on the local network and VPN addresses such as Tailscale or ZeroTier. It never answers the internet.

## Building

```powershell
.\build.ps1
```

This needs only Windows, because it uses the built-in C# compiler. If [Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed, it also builds `dist\SPEControl-Setup.exe`.

To try it without the amplifier, run `dist\SPEControl.exe --simulate`.

## Layout

- `server/SpeControl.cs`: tray app, USB link to the amp, and web server
- `web/index.html`: the control panel. It can also be opened directly in Chrome or Edge to connect over USB without the app.
- `installer/SPEControl.iss`: the installer script

> Not yet verified against the real amplifier. The protocol details were written from memory of SPE's programmer's guide.
