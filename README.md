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

## Mini panel

For when the full panel takes too much screen next to the logger. Right-click the tray icon, choose **Open mini panel**, then:

- **Tall strip**: a narrow column to sit beside the logger.
- **Wide bar**: a short bar to sit above or below it.

Both show Operate/Standby, TX, output with peak, SWR, temperature, band, power level and the current warning. The ⇅ button switches between the two, and ⤢ opens the full panel. It opens in a small Edge window with no address bar. Windows can't keep a browser window always on top, so park it where the logger doesn't cover it. Any browser can open it too: add `?mini=strip` or `?mini=bar` to the panel address.

## Phones and tablets

Connect to the same Wi-Fi as the computer, open the browser and type the address shown, for example `http://192.168.1.188:8080`. "Add to Home Screen" makes it behave like an app. This works on iPad and iPhone too.

The app answers only devices on the local network and VPN addresses such as Tailscale or ZeroTier. It never answers the internet.

## Amp follows the radio (TTTLog, N1MM+ and others)

SPE Control listens for N1MM-compatible **RadioInfo** packets on UDP port 12060. When the radio moves to another band, the app presses the amp's Band keys until the amp matches.

- It never changes band while the amp or the radio is transmitting.
- It ignores frequencies outside the ham bands, so tuning to WWV won't move the amp.
- You can turn it off with **"Amp follows radio"** on the panel, or in the tray menu.
- While it's on, the manual band buttons are locked, so the two don't fight.

**TTTLog setup:**
1. Go to **Config ▸ Rotor & N1MM broadcasts**.
2. Tick **RadioInfo**. The default target, `127.0.0.1:12060`, is right when TTTLog runs on the same PC as SPE Control.
3. If TTTLog runs on a different PC, add that PC's address, for example `127.0.0.1:12060 192.168.1.188:12060`.

To test without a radio, run `.\tools\send-radioinfo.ps1 -kHz 7025`.

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
