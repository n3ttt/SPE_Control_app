// SPE Control - talks to an SPE Expert amplifier over USB and serves the
// control panel to any browser on the local network.
// Written for the C# 5 compiler that ships with Windows (.NET Framework 4.x).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Expert Amp Control")]
[assembly: AssemblyProduct("Expert Amp Control")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

static class Program
{
    // AppName is the internal id (startup registry value, settings folder): keep it so upgrades still work.
    public const string AppName = "SPE Control";
    public const string DisplayName = "Expert Amp Control";
    public const string MutexName = "SPEControlPanelServer";

    [STAThread]
    static void Main(string[] args)
    {
        bool startup = Array.IndexOf(args, "--startup") >= 0;
        bool simulate = Array.IndexOf(args, "--simulate") >= 0;

        bool created;
        Mutex mutex = new Mutex(true, MutexName, out created);
        if (!created)
        {
            // Already running: just open the panel.
            Settings.Load();
            if (!startup) OpenBrowser("http://localhost:" + Settings.HttpPort + "/");
            return;
        }

        Settings.Load();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        RadioListener radio = new RadioListener();
        radio.Start();
        AmpLink amp = new AmpLink(simulate, radio);
        amp.Start();

        WebServer web = new WebServer(amp, Settings.HttpPort);
        try { web.Start(); }
        catch (Exception ex)
        {
            MessageBox.Show("Expert Amp Control could not start its web server on port " + Settings.HttpPort +
                ".\n\nAnother program may be using that port.\n\n" + ex.Message,
                DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new TrayContext(amp, web, startup));
        GC.KeepAlive(mutex);
    }

    public static void OpenBrowser(string url)
    {
        try { Process.Start(url); } catch { }
    }

    public static List<string> LanUrls()
    {
        // Home Wi-Fi / Ethernet first, VPN and virtual adapters (ZeroTier, Tailscale, Hyper-V...) after.
        List<KeyValuePair<int, string>> ranked = new List<KeyValuePair<int, string>>();
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties p = ni.GetIPProperties();
                string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                bool physical = (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                 ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) &&
                                !Regex.IsMatch(d, "virtual|zerotier|tailscale|vpn|hyper-v|vmware|virtualbox|wsl|tap|tun|wireguard|loopback");
                int rank = (physical ? 0 : 2) + (p.GatewayAddresses.Count > 0 ? 0 : 1);
                foreach (UnicastIPAddressInformation a in p.UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (!WebServer.IsLocalAddress(a.Address) || IPAddress.IsLoopback(a.Address)) continue;
                    if (a.Address.ToString().StartsWith("169.254.")) continue;
                    ranked.Add(new KeyValuePair<int, string>(rank, "http://" + a.Address + ":" + Settings.HttpPort + "/"));
                }
            }
        }
        catch { }
        List<string> list = new List<string>();
        for (int r = 0; r < 4; r++)
            foreach (KeyValuePair<int, string> kv in ranked) if (kv.Key == r) list.Add(kv.Value);
        return list;
    }
}

// ---------------------------------------------------------------- settings
static class Settings
{
    public static string Port = "auto";
    public static int HttpPort = 8080;
    public static int Baud = 115200;
    public static bool Follow = true;        // amp follows the radio's band (from logger RadioInfo packets)
    public static int RadioPort = 12060;     // N1MM-compatible RadioInfo UDP port

    static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SPE Control"); } }
    static string FilePath { get { return Path.Combine(Dir, "settings.txt"); } }

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int i = line.IndexOf('=');
                if (i < 0) continue;
                string k = line.Substring(0, i).Trim().ToLowerInvariant(), v = line.Substring(i + 1).Trim();
                int n;
                if (k == "port" && v.Length > 0) Port = v;
                else if (k == "httpport" && int.TryParse(v, out n)) HttpPort = n;
                else if (k == "baud" && int.TryParse(v, out n)) Baud = n;
                else if (k == "follow") Follow = v != "0" && v.ToLowerInvariant() != "false";
                else if (k == "radioport" && int.TryParse(v, out n)) RadioPort = n;
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, "port=" + Port + "\r\nhttpport=" + HttpPort + "\r\nbaud=" + Baud +
                "\r\nfollow=" + (Follow ? "1" : "0") + "\r\nradioport=" + RadioPort + "\r\n");
        }
        catch { }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartWithWindows
    {
        get
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(Program.AppName) != null;
        }
        set
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (value) k.SetValue(Program.AppName, "\"" + Application.ExecutablePath + "\" --startup");
                else k.DeleteValue(Program.AppName, false);
            }
        }
    }
}

// ---------------------------------------------------------------- amplifier link
class AmpLink
{
    readonly object lk = new object();
    readonly Queue<byte> cmds = new Queue<byte>();
    readonly List<byte> buf = new List<byte>();
    readonly bool simulate;

    string portName;
    string state = "Looking for the amplifier...";
    string lastFrame;
    DateTime lastFrameAt = DateTime.MinValue;
    volatile bool reconnect;
    int autoIndex;
    Simulator sim;

    public readonly RadioListener Radio;
    string followFor, followNote = "";
    int followPresses;
    DateTime lastPress = DateTime.MinValue;

    public AmpLink(bool simulate, RadioListener radio) { this.simulate = simulate; Radio = radio; }

    public string FollowNote { get { lock (lk) return followNote; } }

    static bool ParseFrame(string frame, out string band, out bool tx)
    {
        band = null; tx = false;
        if (frame == null) return false;
        string[] f = frame.Split(',');
        for (int i = 0; i + 5 < f.Length; i++)
        {
            if (Regex.IsMatch(f[i].Trim(), @"^\d\dK$"))
            {
                tx = f[i + 2].Trim() == "T";
                band = f[i + 5].Trim();
                return true;
            }
        }
        return false;
    }

    // Band-follow: decide whether to press Band+ or Band- so the amp matches the radio.
    // One press at a time, only after a fresh status frame, never while either side transmits.
    byte? FollowKey()
    {
        double khz, age; string md, app; bool rtx;
        if (!Settings.Follow || Radio == null || !Radio.Get(out khz, out md, out rtx, out age, out app) || age > 30)
        {
            lock (lk) followNote = "";
            return null;
        }
        string want = RadioListener.BandFor(khz);
        string frame; DateTime frameAt;
        lock (lk) { frame = lastFrame; frameAt = lastFrameAt; }
        string band; bool atx;
        if (want == null || (DateTime.UtcNow - frameAt).TotalSeconds > 2 || !ParseFrame(frame, out band, out atx)) return null;

        if (want != followFor) { followFor = want; followPresses = 0; }
        if (band == want) { lock (lk) followNote = ""; return null; }
        if (atx || rtx) return null;
        if (followPresses >= 12)
        {
            lock (lk) followNote = "Couldn't move the amp to the radio's band. Check that band is enabled on the amp.";
            return null;
        }
        if (frameAt <= lastPress || (DateTime.UtcNow - lastPress).TotalMilliseconds < 350) return null;
        followPresses++;
        lastPress = DateTime.UtcNow;
        lock (lk) followNote = "Changing band to match the radio...";
        return string.CompareOrdinal(want, band) > 0 ? (byte)0x03 : (byte)0x02;
    }

    public void Start()
    {
        Thread t = new Thread(simulate ? (ThreadStart)RunSim : Run);
        t.IsBackground = true;
        t.Name = "AmpLink";
        t.Start();
    }

    public void Reconnect() { reconnect = true; }

    public void Enqueue(byte c)
    {
        lock (lk) { if (cmds.Count < 20) cmds.Enqueue(c); }
    }

    public void Snapshot(out string frame, out double age, out string st, out string port)
    {
        lock (lk)
        {
            frame = lastFrame;
            age = lastFrameAt == DateTime.MinValue ? 9999 : (DateTime.UtcNow - lastFrameAt).TotalSeconds;
            st = state;
            port = portName;
        }
    }

    void SetState(string port, string s) { lock (lk) { portName = port; state = s; } }

    byte? NextCmd()
    {
        lock (lk) { if (cmds.Count > 0) return cmds.Dequeue(); }
        return null;
    }

    static byte[] Packet(byte c) { return new byte[] { 0x55, 0x55, 0x55, 0x01, c, c }; }

    void RunSim()
    {
        sim = new Simulator();
        SetState("SIM", "Simulated amplifier (test mode)");
        while (true)
        {
            byte? c = NextCmd();
            if (c.HasValue) sim.Key(c.Value);
            byte? fk = FollowKey();
            if (fk.HasValue) sim.Key(fk.Value);
            string f = sim.Frame();
            lock (lk) { lastFrame = f; lastFrameAt = DateTime.UtcNow; }
            Thread.Sleep(200);
        }
    }

    void Run()
    {
        while (true)
        {
            SerialPort sp = null;
            string p = null;
            try
            {
                reconnect = false;
                p = ChoosePort();
                if (p == null)
                {
                    SetState(null, "Amplifier not found. Check the USB cable is plugged in.");
                    Sleep(3000);
                    continue;
                }
                sp = new SerialPort(p, Settings.Baud, Parity.None, 8, StopBits.One);
                sp.Handshake = Handshake.None;
                sp.ReadTimeout = 100;
                sp.WriteTimeout = 1000;
                sp.Open();
                buf.Clear();
                SetState(p, "Connected on " + p + ", waiting for the amplifier to answer...");

                DateTime opened = DateTime.UtcNow, lastPoll = DateTime.MinValue;
                bool everAnswered = false;
                while (!reconnect)
                {
                    byte? c = NextCmd();
                    if (!c.HasValue) c = FollowKey();
                    if (c.HasValue) { sp.Write(Packet(c.Value), 0, 6); Thread.Sleep(40); }

                    if ((DateTime.UtcNow - lastPoll).TotalMilliseconds >= 200)
                    {
                        sp.Write(Packet(0x90), 0, 6);
                        lastPoll = DateTime.UtcNow;
                    }

                    int n = sp.BytesToRead;
                    if (n > 0)
                    {
                        byte[] b = new byte[n];
                        int r = sp.Read(b, 0, n);
                        if (Feed(b, r)) everAnswered = true;
                    }
                    else Thread.Sleep(15);

                    double age;
                    lock (lk) age = (DateTime.UtcNow - lastFrameAt).TotalSeconds;
                    if (age > 3)
                    {
                        SetState(p, "Connected on " + p + ", but the amplifier is not answering. Is it switched on?");
                        // In automatic mode, if this port never answered, try the next USB-serial port.
                        if (!everAnswered && Settings.Port == "auto" && (DateTime.UtcNow - opened).TotalSeconds > 6)
                        {
                            autoIndex++;
                            break;
                        }
                    }
                    else SetState(p, "Connected on " + p);
                }
            }
            catch (Exception ex)
            {
                SetState(p, "Lost the USB connection (" + ex.Message + "). Retrying...");
                Sleep(2000);
            }
            finally
            {
                if (sp != null) { try { sp.Close(); } catch { } sp.Dispose(); }
            }
        }
    }

    void Sleep(int ms)
    {
        for (int i = 0; i < ms / 100 && !reconnect; i++) Thread.Sleep(100);
    }

    // Returns true when at least one complete status frame was decoded.
    bool Feed(byte[] bytes, int count)
    {
        bool got = false;
        for (int i = 0; i < count; i++) buf.Add(bytes[i]);
        while (true)
        {
            int s = -1;
            for (int i = 0; i + 2 < buf.Count; i++)
                if (buf[i] == 0xAA && buf[i + 1] == 0xAA && buf[i + 2] == 0xAA) { s = i; break; }
            if (s < 0) { if (buf.Count > 1024) buf.RemoveRange(0, buf.Count - 2); return got; }
            if (buf.Count < s + 4) return got;
            int len = buf[s + 3];
            if (buf.Count < s + 4 + len + 2) return got;
            byte[] payload = buf.GetRange(s + 4, len).ToArray();
            buf.RemoveRange(0, s + 4 + len + 2);
            while (buf.Count > 0 && (buf[0] == 0x0D || buf[0] == 0x0A)) buf.RemoveAt(0);
            string text = Encoding.ASCII.GetString(payload);
            if (text.IndexOf(',') >= 0)
            {
                lock (lk) { lastFrame = text; lastFrameAt = DateTime.UtcNow; }
                got = true;
            }
        }
    }

    // Pick the serial port: the one chosen by the user, otherwise an FTDI USB-serial port.
    // Ports from other makers are never probed automatically, so other gear is left alone.
    string ChoosePort()
    {
        string[] all = SerialPort.GetPortNames();
        if (Settings.Port != "auto")
            return Array.IndexOf(all, Settings.Port) >= 0 ? Settings.Port : null;
        List<string> ftdi = FtdiPorts();
        if (ftdi.Count == 0) return null;
        return ftdi[autoIndex % ftdi.Count];
    }

    public static List<string> FtdiPorts()
    {
        List<string> list = new List<string>();
        try
        {
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%VID_0403%'"))
            {
                foreach (ManagementObject o in s.Get())
                {
                    string name = Convert.ToString(o["Name"]);
                    Match m = Regex.Match(name ?? "", @"\((COM\d+)\)");
                    if (m.Success && !list.Contains(m.Groups[1].Value)) list.Add(m.Groups[1].Value);
                }
            }
        }
        catch { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public static List<KeyValuePair<string, string>> AllPorts()
    {
        Dictionary<string, string> names = new Dictionary<string, string>();
        try
        {
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
            {
                foreach (ManagementObject o in s.Get())
                {
                    string name = Convert.ToString(o["Name"]);
                    Match m = Regex.Match(name ?? "", @"\((COM\d+)\)");
                    if (m.Success) names[m.Groups[1].Value] = name;
                }
            }
        }
        catch { }
        List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
        foreach (string p in SerialPort.GetPortNames())
        {
            string n;
            list.Add(new KeyValuePair<string, string>(p, names.TryGetValue(p, out n) ? n : p));
        }
        list.Sort(delegate(KeyValuePair<string, string> a, KeyValuePair<string, string> b)
        {
            return ComNum(a.Key).CompareTo(ComNum(b.Key));
        });
        return list;
    }

    static int ComNum(string p) { int n; int.TryParse(p.Replace("COM", ""), out n); return n; }
}

// Fake amplifier used with --simulate, so everything can be tried without the real amp.
class Simulator
{
    static readonly string[] Bands = { "00", "01", "02", "03", "04", "05", "06", "07", "08", "09", "10" };
    static readonly double[] Swr = { 1.9, 1.3, 1.6, 1.15, 1.4, 1.2, 1.35, 1.25, 1.5, 1.3, 1.7 };
    readonly Random rnd = new Random();
    bool op, tx; int band = 5, ant = 1; string level = "M", input = "1"; double temp = 31;
    DateTime txUntil, nextTx = DateTime.UtcNow.AddSeconds(2);

    public void Key(byte c)
    {
        switch (c)
        {
            case 0x0D: op = !op; break;
            case 0x03: if (!tx && band < Bands.Length - 1) band++; break;
            case 0x02: if (!tx && band > 0) band--; break;
            case 0x0B: level = level == "L" ? "M" : level == "M" ? "H" : "L"; break;
            case 0x04: ant = ant % 4 + 1; break;
            case 0x01: input = input == "1" ? "2" : "1"; break;
        }
    }

    public string Frame()
    {
        DateTime now = DateTime.UtcNow;
        if (!tx && now > nextTx) { tx = true; txUntil = now.AddSeconds(1.5 + rnd.NextDouble() * 4); }
        if (tx && now > txUntil) { tx = false; nextTx = now.AddSeconds(1.5 + rnd.NextDouble() * 4.5); }
        bool keyed = tx && op;
        int max = level == "L" ? 500 : level == "M" ? 1000 : 1450;
        int pout = keyed ? (int)(max * (0.6 + 0.4 * rnd.NextDouble())) : 0;
        double swr = tx ? Swr[band] + rnd.NextDouble() * 0.04 : 0;
        temp += keyed ? pout / 9000.0 : -(temp - 31) / 60;
        string warn = temp > 58 ? "O" : "N";
        System.Globalization.CultureInfo ic = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join(",", new string[] {
            "", "15K", op ? "O" : "S", tx ? "T" : "R", "A", input, Bands[band], ant + "a", "0r", level,
            pout.ToString("0000", ic), (tx ? 1.06 : 0).ToString("00.00", ic), swr.ToString("00.00", ic),
            (keyed ? 46.5 : 48.2).ToString("00.0", ic), (keyed ? pout / 45.0 + 2 : 0).ToString("00.0", ic),
            ((int)temp).ToString("000", ic), "000", "000", warn, "N", "" });
    }
}

// ---------------------------------------------------------------- radio (logger) link
// Listens for N1MM Logger+ compatible <RadioInfo> UDP packets, as sent by TTTLog
// (Config > Rotor & N1MM broadcasts), N1MM+, DXLog and others.
class RadioListener
{
    readonly object lk = new object();
    double khz; string mode = ""; bool tx; string app = "";
    DateTime at = DateTime.MinValue;
    public string Error;

    public void Start()
    {
        Thread t = new Thread(Run);
        t.IsBackground = true;
        t.Name = "RadioListener";
        t.Start();
    }

    void Run()
    {
        UdpClient u;
        try
        {
            u = new UdpClient();
            // Share the port with other listeners (rotor/antenna programs) on this computer.
            u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            u.Client.Bind(new IPEndPoint(IPAddress.Any, Settings.RadioPort));
        }
        catch (Exception ex)
        {
            Error = "Can't listen for the radio on UDP port " + Settings.RadioPort + ": " + ex.Message;
            return;
        }
        while (true)
        {
            try
            {
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = u.Receive(ref ep);
                if (!WebServer.IsLocalAddress(ep.Address)) continue;
                Parse(Encoding.UTF8.GetString(data));
            }
            catch { Thread.Sleep(100); }
        }
    }

    void Parse(string xml)
    {
        if (xml.IndexOf("<RadioInfo>", StringComparison.Ordinal) < 0) return;
        Dictionary<string, string> f = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(xml, @"<(\w+)>([^<]*)</\1>")) f[m.Groups[1].Value] = m.Groups[2].Value;
        string s;
        // In SO2R, only follow the radio that is currently active.
        if (f.TryGetValue("ActiveRadioNr", out s) && f.ContainsKey("RadioNr") && s != f["RadioNr"] && s != "0") return;
        long tens;
        string fs = f.TryGetValue("TXFreq", out s) && s.Length > 0 && s != "0" ? s : (f.TryGetValue("Freq", out s) ? s : null);
        if (fs == null || !long.TryParse(fs, out tens) || tens <= 0) return;
        lock (lk)
        {
            khz = tens / 100.0;
            mode = f.TryGetValue("Mode", out s) ? s : "";
            tx = f.TryGetValue("IsTransmitting", out s) && s.Equals("True", StringComparison.OrdinalIgnoreCase);
            app = f.TryGetValue("app", out s) ? s : "";
            at = DateTime.UtcNow;
        }
    }

    // age = seconds since the last packet (loggers repeat every ~10 s, and on every change)
    public bool Get(out double kHz, out string md, out bool transmitting, out double age, out string from)
    {
        lock (lk)
        {
            kHz = khz; md = mode; transmitting = tx; from = app;
            age = at == DateTime.MinValue ? 9999 : (DateTime.UtcNow - at).TotalSeconds;
            return at != DateTime.MinValue;
        }
    }

    // Expert band code for a frequency, or null when it's outside the ham bands the amp covers.
    public static string BandFor(double k)
    {
        double[,] edges = { { 1800, 2000 }, { 3500, 4000 }, { 5250, 5450 }, { 7000, 7300 }, { 10100, 10150 }, { 14000, 14350 },
                            { 18068, 18168 }, { 21000, 21450 }, { 24890, 24990 }, { 28000, 29700 }, { 50000, 54000 } };
        for (int i = 0; i <= edges.GetUpperBound(0); i++)
            if (k >= edges[i, 0] && k <= edges[i, 1]) return i.ToString("00");
        return null;
    }
}

// ---------------------------------------------------------------- web server
class WebServer
{
    readonly AmpLink amp;
    readonly int port;
    TcpListener listener;
    byte[] page;
    List<string> urls = new List<string>();
    DateTime urlsAt = DateTime.MinValue;

    public WebServer(AmpLink amp, int port) { this.amp = amp; this.port = port; }

    public void Start()
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html"))
        using (MemoryStream ms = new MemoryStream())
        {
            s.CopyTo(ms);
            page = ms.ToArray();
        }
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Thread t = new Thread(AcceptLoop);
        t.IsBackground = true;
        t.Start();
    }

    public List<string> Urls
    {
        get
        {
            if ((DateTime.UtcNow - urlsAt).TotalSeconds > 30) { urls = Program.LanUrls(); urlsAt = DateTime.UtcNow; }
            return urls;
        }
    }

    void AcceptLoop()
    {
        while (true)
        {
            try
            {
                TcpClient c = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(Handle, c);
            }
            catch { Thread.Sleep(200); }
        }
    }

    // Only answer devices on the home network (private address ranges), never the internet.
    public static bool IsLocalAddress(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork) return a.IsIPv6LinkLocal;
        byte[] b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    void Handle(object state)
    {
        TcpClient client = (TcpClient)state;
        try
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            NetworkStream ns = client.GetStream();
            IPAddress remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
            if (!IsLocalAddress(remote)) { Send(ns, 403, "text/plain", Encoding.ASCII.GetBytes("Forbidden")); return; }

            string head = ReadHead(ns);
            if (head == null) return;
            string[] first = head.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
            if (first.Length < 2) return;
            string method = first[0].ToUpperInvariant();
            string target = first[1];
            string path = target, query = "";
            int q = target.IndexOf('?');
            if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }

            if (method == "GET" && (path == "/" || path == "/index.html"))
                Send(ns, 200, "text/html; charset=utf-8", page);
            else if (method == "GET" && path == "/api/status")
                Send(ns, 200, "application/json", Encoding.UTF8.GetBytes(StatusJson()));
            else if (method == "POST" && path == "/api/cmd")
            {
                int c;
                string v = QueryValue(query, "c");
                if (v != null && int.TryParse(v, out c) && c >= 0x01 && c <= 0x11)
                {
                    amp.Enqueue((byte)c);
                    Send(ns, 200, "application/json", Encoding.ASCII.GetBytes("{\"ok\":true}"));
                }
                else Send(ns, 400, "application/json", Encoding.ASCII.GetBytes("{\"ok\":false}"));
            }
            else if (method == "POST" && path == "/api/follow")
            {
                Settings.Follow = QueryValue(query, "on") == "1";
                Settings.Save();
                Send(ns, 200, "application/json", Encoding.ASCII.GetBytes("{\"ok\":true}"));
            }
            else Send(ns, 404, "text/plain", Encoding.ASCII.GetBytes("Not found"));
        }
        catch { }
        finally { try { client.Close(); } catch { } }
    }

    static string ReadHead(NetworkStream ns)
    {
        byte[] b = new byte[8192];
        int total = 0;
        while (total < b.Length)
        {
            int r = ns.Read(b, total, b.Length - total);
            if (r <= 0) break;
            total += r;
            string s = Encoding.ASCII.GetString(b, 0, total);
            if (s.Contains("\r\n\r\n")) return s;
        }
        return total > 0 ? Encoding.ASCII.GetString(b, 0, total) : null;
    }

    static string QueryValue(string query, string key)
    {
        foreach (string part in query.Split('&'))
        {
            int i = part.IndexOf('=');
            if (i > 0 && part.Substring(0, i) == key) return Uri.UnescapeDataString(part.Substring(i + 1));
        }
        return null;
    }

    string StatusJson()
    {
        string frame, st, p; double age;
        amp.Snapshot(out frame, out age, out st, out p);
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"server\":true,\"state\":").Append(J(st));
        sb.Append(",\"port\":").Append(J(p));
        sb.Append(",\"frame\":").Append(J(frame));
        sb.Append(",\"age\":").Append(Math.Round(Math.Min(age, 9999), 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        System.Globalization.CultureInfo ic = System.Globalization.CultureInfo.InvariantCulture;
        double khz, rage; string md, app; bool rtx;
        sb.Append(",\"follow\":").Append(Settings.Follow ? "true" : "false");
        sb.Append(",\"followNote\":").Append(J(amp.FollowNote));
        sb.Append(",\"radioError\":").Append(J(amp.Radio.Error));
        if (amp.Radio.Get(out khz, out md, out rtx, out rage, out app))
            sb.Append(",\"radio\":{\"khz\":").Append(khz.ToString("0.##", ic))
              .Append(",\"mode\":").Append(J(md)).Append(",\"tx\":").Append(rtx ? "true" : "false")
              .Append(",\"band\":").Append(J(RadioListener.BandFor(khz)))
              .Append(",\"app\":").Append(J(app))
              .Append(",\"age\":").Append(Math.Round(Math.Min(rage, 9999), 1).ToString(ic)).Append('}');
        else sb.Append(",\"radio\":null");
        sb.Append(",\"urls\":[");
        List<string> u = Urls;
        for (int i = 0; i < u.Count; i++) { if (i > 0) sb.Append(','); sb.Append(J(u[i])); }
        sb.Append("]}");
        return sb.ToString();
    }

    static string J(string s)
    {
        if (s == null) return "null";
        StringBuilder sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }

    static void Send(NetworkStream ns, int code, string type, byte[] body)
    {
        string reason = code == 200 ? "OK" : code == 400 ? "Bad Request" : code == 403 ? "Forbidden" : "Not Found";
        string h = "HTTP/1.1 " + code + " " + reason + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                   "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        byte[] hb = Encoding.ASCII.GetBytes(h);
        ns.Write(hb, 0, hb.Length);
        ns.Write(body, 0, body.Length);
    }
}

// ---------------------------------------------------------------- tray icon
class TrayContext : ApplicationContext
{
    readonly AmpLink amp;
    readonly WebServer web;
    readonly NotifyIcon icon;
    readonly ToolStripMenuItem statusItem, addrItem, portMenu, startupItem, followItem;
    readonly System.Windows.Forms.Timer timer;

    public TrayContext(AmpLink amp, WebServer web, bool startup)
    {
        this.amp = amp;
        this.web = web;

        ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem open = new ToolStripMenuItem("Open control panel", null, delegate { OpenPanel(); });
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        ToolStripMenuItem mini = new ToolStripMenuItem("Open mini panel");
        mini.DropDownItems.Add(new ToolStripMenuItem("Tall strip (beside the logger)", null, delegate { OpenMini("strip"); }));
        mini.DropDownItems.Add(new ToolStripMenuItem("Wide bar (above or below the logger)", null, delegate { OpenMini("bar"); }));
        menu.Items.Add(mini);
        addrItem = new ToolStripMenuItem("Use on a phone or tablet...", null, delegate { ShowAddress(); });
        menu.Items.Add(addrItem);
        menu.Items.Add(new ToolStripSeparator());
        statusItem = new ToolStripMenuItem("Starting...");
        statusItem.Enabled = false;
        menu.Items.Add(statusItem);
        portMenu = new ToolStripMenuItem("USB port");
        portMenu.DropDownOpening += delegate { FillPorts(); };
        portMenu.DropDownItems.Add("(loading)");
        menu.Items.Add(portMenu);
        startupItem = new ToolStripMenuItem("Start automatically with Windows", null, delegate
        {
            try { Settings.StartWithWindows = !Settings.StartWithWindows; } catch { }
            startupItem.Checked = Settings.StartWithWindows;
        });
        startupItem.Checked = Settings.StartWithWindows;
        followItem = new ToolStripMenuItem("Amp follows the radio (TTTLog / N1MM)", null, delegate
        {
            Settings.Follow = !Settings.Follow;
            Settings.Save();
        });
        menu.Items.Add(followItem);
        menu.Opening += delegate { followItem.Checked = Settings.Follow; };
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitApp(); }));

        icon = new NotifyIcon();
        try { icon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { icon.Icon = SystemIcons.Application; }
        icon.Text = Program.DisplayName;
        icon.ContextMenuStrip = menu;
        icon.Visible = true;
        icon.DoubleClick += delegate { OpenPanel(); };

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 1000;
        timer.Tick += delegate { UpdateStatus(); };
        timer.Start();
        UpdateStatus();

        if (!startup) OpenPanel();
        icon.ShowBalloonTip(5000, Program.DisplayName + " is running",
            "Double-click this icon to open the control panel. Right-click for phone/tablet setup.", ToolTipIcon.Info);
    }

    void UpdateStatus()
    {
        string frame, st, p; double age;
        amp.Snapshot(out frame, out age, out st, out p);
        statusItem.Text = (age < 3 ? "Amplifier: connected" : "Amplifier: ") + (age < 3 ? (p != null ? " (" + p + ")" : "") : Shorten(st));
        string tip = Program.DisplayName + " - " + (age < 3 ? "amp connected" : "amp not connected");
        icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    static string Shorten(string s) { return s.Length > 70 ? s.Substring(0, 67) + "..." : s; }

    void FillPorts()
    {
        portMenu.DropDownItems.Clear();
        ToolStripMenuItem auto = new ToolStripMenuItem("Automatic (recommended)", null, delegate { SetPort("auto"); });
        auto.Checked = Settings.Port == "auto";
        portMenu.DropDownItems.Add(auto);
        portMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (KeyValuePair<string, string> kv in AmpLink.AllPorts())
        {
            string name = kv.Key;
            ToolStripMenuItem it = new ToolStripMenuItem(kv.Value, null, delegate { SetPort(name); });
            it.Checked = Settings.Port == name;
            portMenu.DropDownItems.Add(it);
        }
    }

    void SetPort(string p)
    {
        Settings.Port = p;
        Settings.Save();
        amp.Reconnect();
    }

    void OpenPanel() { Program.OpenBrowser("http://localhost:" + Settings.HttpPort + "/"); }

    // Opens the compact panel in its own small window (Edge "app" window: no tabs or address bar).
    // Falls back to the default browser if Edge isn't found.
    void OpenMini(string layout)
    {
        string url = "http://localhost:" + Settings.HttpPort + "/?mini=" + layout;
        string size = layout == "bar" ? "1000,130" : "300,440";
        try { Process.Start("msedge", "--app=" + url + " --window-size=" + size); }
        catch { Program.OpenBrowser(url); }
    }

    void ShowAddress()
    {
        List<string> u = Program.LanUrls();
        string msg = u.Count == 0
            ? "This computer doesn't seem to be on a network right now."
            : "On the phone or tablet, connect to the same Wi-Fi as this computer, open the web browser and type:\n\n    " +
              string.Join("\n    ", u.ToArray()) + "\n\nTip: add it to the home screen so it works like an app.\n\nCopy the address to the clipboard?";
        if (MessageBox.Show(msg, Program.DisplayName, u.Count == 0 ? MessageBoxButtons.OK : MessageBoxButtons.YesNo,
                MessageBoxIcon.Information) == DialogResult.Yes)
        {
            try { Clipboard.SetText(u[0]); } catch { }
        }
    }

    void ExitApp()
    {
        if (MessageBox.Show("Stop Expert Amp Control?\n\nPhones, tablets and this computer will no longer be able to control the amplifier until it is started again.",
                Program.DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        icon.Visible = false;
        icon.Dispose();
        timer.Stop();
        ExitThread();
        Environment.Exit(0);
    }
}
