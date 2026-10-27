using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AntigravityProxyManager
{
    internal static class Program
    {
        internal const string MutexName = "Local\\AntigravityProxyManager-7C1D2F9A";
        internal const string ShowEventName = "Local\\AntigravityProxyManager-Show-7C1D2F9A";
        internal const string LaunchEventName = "Local\\AntigravityProxyManager-Launch-7C1D2F9A";

        [STAThread]
        private static void Main(string[] args)
        {
            bool startInTray = false;
            bool launchNow = false;
            foreach (string a in args)
            {
                if (string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)) startInTray = true;
                if (string.Equals(a, "--launch", StringComparison.OrdinalIgnoreCase))
                {
                    startInTray = true;
                    launchNow = true;
                }
            }

            bool createdNew = false;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    SignalRunningInstance(launchNow);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                using (EventWaitHandle launchEvent = new EventWaitHandle(false, EventResetMode.AutoReset, LaunchEventName))
                {
                    MainForm form = new MainForm(startInTray, launchNow);
                    Thread listener = new Thread(delegate()
                    {
                        WaitHandle[] handles = new WaitHandle[] { showEvent, launchEvent };
                        while (true)
                        {
                            int idx;
                            try { idx = WaitHandle.WaitAny(handles); }
                            catch { break; }
                            try { form.ActivateFromExternal(idx == 1); }
                            catch { }
                        }
                    });
                    listener.IsBackground = true;
                    listener.Start();
                    Application.Run(form);
                }
                GC.KeepAlive(mutex);
            }
        }

        private static void SignalRunningInstance(bool launch)
        {
            try
            {
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(launch ? LaunchEventName : ShowEventName, out ev))
                {
                    ev.Set();
                    ev.Dispose();
                }
            }
            catch { }
        }
    }

    internal sealed class ProxyConfig
    {
        public string Type = "HTTP";
        public string Host = "127.0.0.1";
        public int Port = 10808;
        public string User = "";
        public string Password = "";
        public bool Enforce = true;
        public bool RunAtLogon = true;
        public bool StartAntigravityAtLogon = false;
        public bool MinimizeToTray = true;
        public bool StopWhenProxyDown = false;

        public bool HasCredentials
        {
            get { return User.Length > 0 || Password.Length > 0; }
        }

        public bool NeedsRelay
        {
            get { return Type == "HTTP" && HasCredentials; }
        }

        public string Url
        {
            get
            {
                string scheme = Type == "SOCKS5" ? "socks5://" : "http://";
                return scheme + Host + ":" + Port.ToString();
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "settings.ini"); }
        }

        public static ProxyConfig Load()
        {
            ProxyConfig c = new ProxyConfig();
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    string host;
                    int port;
                    if (DetectSystemProxy(out host, out port))
                    {
                        c.Host = host;
                        c.Port = port;
                    }
                    return c;
                }

                Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(ConfigPath))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                    int i = t.IndexOf('=');
                    if (i <= 0) continue;
                    d[t.Substring(0, i).Trim()] = t.Substring(i + 1).Trim();
                }

                c.Type = Get(d, "type", c.Type);
                if (c.Type != "HTTP" && c.Type != "SOCKS5") c.Type = "HTTP";
                c.Host = Get(d, "host", c.Host);
                c.Port = ParseInt(Get(d, "port", c.Port.ToString()), c.Port);
                c.User = Get(d, "user", "");
                c.Password = Unprotect(Get(d, "pass", ""));
                c.Enforce = ParseBool(Get(d, "enforce", "1"), true);
                c.RunAtLogon = ParseBool(Get(d, "runAtLogon", "1"), true);
                c.StartAntigravityAtLogon = ParseBool(Get(d, "startAntigravityAtLogon", "0"), false);
                c.MinimizeToTray = ParseBool(Get(d, "minimizeToTray", "1"), true);
                c.StopWhenProxyDown = ParseBool(Get(d, "stopWhenProxyDown", "0"), false);
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Antigravity Proxy Manager");
                sb.AppendLine("type=" + Type);
                sb.AppendLine("host=" + Host);
                sb.AppendLine("port=" + Port.ToString());
                sb.AppendLine("user=" + User);
                sb.AppendLine("pass=" + Protect(Password));
                sb.AppendLine("enforce=" + (Enforce ? "1" : "0"));
                sb.AppendLine("runAtLogon=" + (RunAtLogon ? "1" : "0"));
                sb.AppendLine("startAntigravityAtLogon=" + (StartAntigravityAtLogon ? "1" : "0"));
                sb.AppendLine("minimizeToTray=" + (MinimizeToTray ? "1" : "0"));
                sb.AppendLine("stopWhenProxyDown=" + (StopWhenProxyDown ? "1" : "0"));
                File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private static string Get(Dictionary<string, string> d, string key, string def)
        {
            string v;
            if (d.TryGetValue(key, out v)) return v;
            return def;
        }

        private static int ParseInt(string s, int def)
        {
            int v;
            if (int.TryParse(s, out v) && v > 0 && v <= 65535) return v;
            return def;
        }

        private static bool ParseBool(string s, bool def)
        {
            if (s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return def;
        }

        private static string Protect(string value)
        {
            if (value.Length == 0) return "";
            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(value);
                byte[] enc = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch { return ""; }
        }

        private static string Unprotect(string value)
        {
            if (value.Length == 0) return "";
            try
            {
                byte[] enc = Convert.FromBase64String(value);
                byte[] raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch { return ""; }
        }

        private static bool DetectSystemProxy(out string host, out int port)
        {
            host = "127.0.0.1";
            port = 10808;
            try
            {
                object raw = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings",
                    "ProxyServer", null);
                string s = raw as string;
                if (string.IsNullOrEmpty(s)) return false;
                string candidate = s;
                if (s.IndexOf('=') >= 0)
                {
                    candidate = null;
                    foreach (string part in s.Split(';'))
                    {
                        int i = part.IndexOf('=');
                        if (i <= 0) continue;
                        string scheme = part.Substring(0, i).Trim();
                        if (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                            scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                        {
                            candidate = part.Substring(i + 1).Trim();
                            break;
                        }
                    }
                    if (candidate == null) return false;
                }
                candidate = candidate.Replace("http://", "").Replace("https://", "");
                int colon = candidate.LastIndexOf(':');
                if (colon <= 0) return false;
                string h = candidate.Substring(0, colon).Trim('[', ']');
                int p;
                if (!int.TryParse(candidate.Substring(colon + 1), out p) || p <= 0 || p > 65535) return false;
                host = h;
                port = p;
                return true;
            }
            catch { return false; }
        }
    }

    internal static class ProxyTester
    {
        public static bool TcpReachable(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        public static string Check(ProxyConfig cfg)
        {
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult ar = client.BeginConnect(cfg.Host, cfg.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(5000)) return "нет соединения с " + cfg.Host + ":" + cfg.Port.ToString();
                client.EndConnect(ar);
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 8000;
                NetworkStream s = client.GetStream();

                if (cfg.Type == "SOCKS5")
                {
                    if (cfg.HasCredentials) return "SOCKS5 с логином и паролем не поддерживается";
                    byte[] greet = new byte[] { 5, 1, 0 };
                    s.Write(greet, 0, greet.Length);
                    s.Flush();
                    byte[] resp = ReadExact(s, 2);
                    if (resp == null) return "SOCKS5-прокси не ответил";
                    if (resp[0] != 5 || resp[1] != 0) return "SOCKS5-прокси отклонил подключение";

                    byte[] hostBytes = Encoding.ASCII.GetBytes("daily-cloudcode-pa.googleapis.com");
                    byte[] req = new byte[7 + hostBytes.Length];
                    req[0] = 5;
                    req[1] = 1;
                    req[2] = 0;
                    req[3] = 3;
                    req[4] = (byte)hostBytes.Length;
                    Buffer.BlockCopy(hostBytes, 0, req, 5, hostBytes.Length);
                    int o = 5 + hostBytes.Length;
                    req[o] = 0x01;
                    req[o + 1] = 0xBB;
                    s.Write(req, 0, req.Length);
                    s.Flush();

                    byte[] head = ReadExact(s, 4);
                    if (head == null) return "SOCKS5-прокси не ответил";
                    if (head[1] != 0) return "SOCKS5-прокси не пускает к daily-cloudcode-pa.googleapis.com (код " + head[1].ToString() + ")";
                    return null;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("CONNECT daily-cloudcode-pa.googleapis.com:443 HTTP/1.1\r\n");
                sb.Append("Host: daily-cloudcode-pa.googleapis.com:443\r\n");
                sb.Append("Proxy-Connection: Keep-Alive\r\n");
                if (cfg.HasCredentials)
                {
                    string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(cfg.User + ":" + cfg.Password));
                    sb.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
                }
                sb.Append("\r\n");
                byte[] request = Encoding.ASCII.GetBytes(sb.ToString());
                s.Write(request, 0, request.Length);
                s.Flush();

                string status = ReadLine(s, 8192);
                if (status == null) return "прокси не ответил";
                if (status.IndexOf(" 200", StringComparison.Ordinal) >= 0) return null;
                if (status.IndexOf(" 407", StringComparison.Ordinal) >= 0) return "прокси требует логин и пароль";
                return "прокси ответил: " + status.Trim();
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private static byte[] ReadExact(Stream s, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int n = s.Read(buf, offset, count - offset);
                if (n <= 0) return null;
                offset += n;
            }
            return buf;
        }

        private static string ReadLine(Stream s, int max)
        {
            List<byte> data = new List<byte>(128);
            while (data.Count < max)
            {
                int b = s.ReadByte();
                if (b < 0) break;
                if (b == '\n') break;
                if (b != '\r') data.Add((byte)b);
            }
            if (data.Count == 0) return null;
            return Encoding.ASCII.GetString(data.ToArray());
        }
    }

    internal sealed class LocalRelay : IDisposable
    {
        private readonly string _upstreamHost;
        private readonly int _upstreamPort;
        private readonly string _authHeader;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _port;

        public int Port { get { return _port; } }

        public LocalRelay(string host, int port, string user, string password)
        {
            _upstreamHost = host;
            _upstreamPort = port;
            if (user.Length > 0 || password.Length > 0)
            {
                string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
                _authHeader = "Proxy-Authorization: Basic " + token + "\r\n";
            }
            else
            {
                _authHeader = "";
            }
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    if (!_running) break;
                    continue;
                }
                Thread t = new Thread(delegate() { HandleClient(client); });
                t.IsBackground = true;
                t.Start();
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 60000;
                    client.SendTimeout = 60000;
                    NetworkStream clientStream = client.GetStream();

                    byte[] headerBytes = ReadHeaders(clientStream);
                    if (headerBytes == null) return;
                    string headerText = Encoding.ASCII.GetString(headerBytes);
                    string firstLine = FirstLine(headerText);
                    string[] parts = firstLine.Split(' ');
                    if (parts.Length < 2) return;
                    string method = parts[0].ToUpperInvariant();
                    string target = parts[1];

                    using (TcpClient upstream = ConnectUpstream())
                    {
                        if (upstream == null)
                        {
                            WriteAll(clientStream, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n");
                            return;
                        }
                        NetworkStream upstreamStream = upstream.GetStream();
                        upstreamStream.ReadTimeout = 15000;
                        upstreamStream.WriteTimeout = 15000;

                        if (method == "CONNECT")
                        {
                            string req = "CONNECT " + target + " HTTP/1.1\r\n"
                                       + "Host: " + target + "\r\n"
                                       + "Proxy-Connection: Keep-Alive\r\n"
                                       + _authHeader
                                       + "\r\n";
                            WriteAll(upstreamStream, req);
                            byte[] proof = ReadHeaders(upstreamStream);
                            if (proof == null)
                            {
                                WriteAll(clientStream, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n");
                                return;
                            }
                            string proofText = Encoding.ASCII.GetString(proof);
                            string status = FirstLine(proofText);
                            if (status.IndexOf(" 200", StringComparison.Ordinal) < 0)
                            {
                                WriteAll(clientStream, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n");
                                return;
                            }
                            WriteAll(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n");
                            SetStreamsInfinite(clientStream, upstreamStream);
                            Pump(clientStream, upstreamStream);
                        }
                        else
                        {
                            string forwarded = InsertAuth(headerText);
                            WriteAll(upstreamStream, forwarded);
                            SetStreamsInfinite(clientStream, upstreamStream);
                            Pump(clientStream, upstreamStream);
                        }
                    }
                }
                catch { }
            }
        }

        private TcpClient ConnectUpstream()
        {
            TcpClient up = new TcpClient();
            try
            {
                IAsyncResult ar = up.BeginConnect(_upstreamHost, _upstreamPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(10000))
                {
                    up.Close();
                    return null;
                }
                up.EndConnect(ar);
                return up;
            }
            catch
            {
                try { up.Close(); } catch { }
                return null;
            }
        }

        private string InsertAuth(string headerText)
        {
            if (_authHeader.Length == 0) return headerText;
            if (headerText.IndexOf("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase) >= 0) return headerText;
            int idx = headerText.LastIndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (idx < 0) return headerText;
            return headerText.Substring(0, idx + 2) + _authHeader + headerText.Substring(idx + 2);
        }

        private static string FirstLine(string text)
        {
            int i = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (i < 0) return text;
            return text.Substring(0, i);
        }

        private static byte[] ReadHeaders(Stream s)
        {
            List<byte> data = new List<byte>(1024);
            int state = 0;
            while (data.Count < 65536)
            {
                int b = s.ReadByte();
                if (b < 0) return null;
                data.Add((byte)b);
                if (b == 13)
                {
                    if (state == 0) state = 1;
                    else if (state == 2) state = 3;
                    else state = 0;
                }
                else if (b == 10)
                {
                    if (state == 1) state = 2;
                    else if (state == 3) return data.ToArray();
                    else state = 0;
                }
                else state = 0;
            }
            return null;
        }

        private static void WriteAll(Stream s, string text)
        {
            byte[] data = Encoding.ASCII.GetBytes(text);
            s.Write(data, 0, data.Length);
            s.Flush();
        }

        private static void SetStreamsInfinite(NetworkStream a, NetworkStream b)
        {
            try { a.ReadTimeout = 0; a.WriteTimeout = 0; } catch { }
            try { b.ReadTimeout = 0; b.WriteTimeout = 0; } catch { }
        }

        private static void Pump(NetworkStream a, NetworkStream b)
        {
            Thread t = new Thread(delegate() { Copy(a, b); });
            t.IsBackground = true;
            t.Start();
            Copy(b, a);
            try { t.Join(1000); } catch { }
        }

        private static void Copy(Stream from, Stream to)
        {
            byte[] buf = new byte[16384];
            try
            {
                while (true)
                {
                    int n = from.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    to.Write(buf, 0, n);
                    to.Flush();
                }
            }
            catch { }
            finally
            {
                try { to.Close(); } catch { }
            }
        }
    }

    internal sealed class AppProc
    {
        public int Pid;
        public int ParentPid;
        public string CommandLine = "";
    }

    internal static class AntigravityProcesses
    {
        public static bool TryGetAll(out List<AppProc> list)
        {
            list = new List<AppProc>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name = 'Antigravity.exe'"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject item in results)
                    {
                        AppProc p = new AppProc();
                        try
                        {
                            p.Pid = Convert.ToInt32(item["ProcessId"]);
                            p.ParentPid = Convert.ToInt32(item["ParentProcessId"]);
                            p.CommandLine = (item["CommandLine"] as string) ?? "";
                        }
                        catch
                        {
                            p.CommandLine = "";
                        }
                        list.Add(p);
                    }
                }
                return true;
            }
            catch
            {
                list = new List<AppProc>();
                return false;
            }
        }

        public static AppProc FindMain(List<AppProc> list)
        {
            List<AppProc> mains = new List<AppProc>();
            foreach (AppProc p in list)
            {
                if (p.CommandLine.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) < 0) mains.Add(p);
            }
            if (mains.Count == 0) return null;
            if (mains.Count == 1) return mains[0];
            AppProc best = mains[0];
            int bestCount = -1;
            foreach (AppProc m in mains)
            {
                int count = 0;
                foreach (AppProc p in list)
                {
                    if (p.ParentPid == m.Pid) count++;
                }
                if (count > bestCount)
                {
                    bestCount = count;
                    best = m;
                }
            }
            return best;
        }
    }

    internal static class AntigravityController
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static string FindExe()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] candidates = new string[]
                {
                    Path.Combine(local, @"Programs\antigravity\Antigravity.exe"),
                    Path.Combine(local, @"Antigravity\Antigravity.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Antigravity\Antigravity.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Antigravity\Antigravity.exe")
                };
                foreach (string candidate in candidates)
                {
                    try { if (File.Exists(candidate)) return candidate; }
                    catch { }
                }

                string[] roots = new string[]
                {
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };
                foreach (string root in roots)
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(root))
                    {
                        string found = FindInUninstall(key);
                        if (found != null) return found;
                    }
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(root))
                    {
                        string found = FindInUninstall(key);
                        if (found != null) return found;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string FindInUninstall(RegistryKey baseKey)
        {
            if (baseKey == null) return null;
            try
            {
                foreach (string name in baseKey.GetSubKeyNames())
                {
                    using (RegistryKey sub = baseKey.OpenSubKey(name))
                    {
                        if (sub == null) continue;
                        string display = sub.GetValue("DisplayName") as string;
                        if (display == null || display.IndexOf("Antigravity", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        string location = sub.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(location))
                        {
                            string exe = Path.Combine(location.Trim().Trim('"'), "Antigravity.exe");
                            if (File.Exists(exe)) return exe;
                        }

                        string icon = sub.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrEmpty(icon))
                        {
                            string exe = icon.Trim();
                            int comma = exe.LastIndexOf(',');
                            if (comma > 0) exe = exe.Substring(0, comma);
                            exe = exe.Trim().Trim('"');
                            if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe)) return exe;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static bool IsRunning()
        {
            try { return Process.GetProcessesByName("Antigravity").Length > 0; }
            catch { return false; }
        }

        public static bool IsRunningThrough(string proxyUrl)
        {
            List<AppProc> list;
            if (!AntigravityProcesses.TryGetAll(out list) || list.Count == 0) return false;
            AppProc main = AntigravityProcesses.FindMain(list);
            if (main == null) return false;
            string needle = "--proxy-server=" + proxyUrl;
            return main.CommandLine.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsUsingRelay(int port)
        {
            List<AppProc> list;
            if (!AntigravityProcesses.TryGetAll(out list)) return false;
            AppProc main = AntigravityProcesses.FindMain(list);
            if (main == null) return false;
            return main.CommandLine.IndexOf("127.0.0.1:" + port.ToString(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static int KillAll()
        {
            int killed = 0;
            foreach (Process p in Process.GetProcessesByName("Antigravity"))
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                        killed++;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            for (int i = 0; i < 50; i++)
            {
                if (!IsRunning()) break;
                Thread.Sleep(200);
            }
            killed += KillHelper("language_server");
            killed += KillHelper("webm_encoder");
            Thread.Sleep(400);
            return killed;
        }

        private static int KillHelper(string name)
        {
            int killed = 0;
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try
                {
                    string path = null;
                    try { path = p.MainModule.FileName; } catch { }
                    bool ours = !string.IsNullOrEmpty(path) &&
                        path.IndexOf("\\antigravity\\", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!ours)
                    {
                        string cmd = GetCommandLine(p.Id);
                        ours = cmd.IndexOf("\\antigravity\\", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    if (ours && !p.HasExited)
                    {
                        p.Kill();
                        killed++;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return killed;
        }

        private static string GetCommandLine(int pid)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid.ToString()))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject item in results)
                    {
                        return (item["CommandLine"] as string) ?? "";
                    }
                }
            }
            catch { }
            return "";
        }

        public static Process Launch(ProxyConfig cfg, string url)
        {
            string exe = FindExe();
            if (string.IsNullOrEmpty(exe))
                throw new InvalidOperationException("Не удалось найти установленное приложение Antigravity.");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = "--proxy-server=" + url;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            try
            {
                psi.EnvironmentVariables["HTTP_PROXY"] = url;
                psi.EnvironmentVariables["HTTPS_PROXY"] = url;
                psi.EnvironmentVariables["ALL_PROXY"] = url;
                psi.EnvironmentVariables["AG_LS_PROXY"] = url;
                psi.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1,::1";
            }
            catch { }
            return Process.Start(psi);
        }

        public static bool ActivateWindow()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("Antigravity"))
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            ShowWindow(p.MainWindowHandle, 9);
                            SetForegroundWindow(p.MainWindowHandle);
                            return true;
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            return false;
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly Color Green = Color.FromArgb(22, 135, 67);
        private static readonly Color Red = Color.FromArgb(190, 40, 40);
        private static readonly Color Orange = Color.FromArgb(190, 110, 0);
        private static readonly Color Gray = Color.FromArgb(95, 99, 104);

        private ProxyConfig cfg;
        private readonly bool startInTray;
        private readonly bool launchOnStart;

        private ComboBox cmbType;
        private TextBox txtHost;
        private NumericUpDown numPort;
        private TextBox txtUser;
        private TextBox txtPass;
        private Button btnCheck;
        private Label lblCheck;
        private Label lblStatus;
        private TextBox txtLog;
        private CheckBox chkEnforce;
        private CheckBox chkRunAtLogon;
        private CheckBox chkStartAtLogon;
        private CheckBox chkMinimize;
        private CheckBox chkStopOnProxyDown;
        private NotifyIcon tray;
        private System.Threading.Timer watchdog;
        private bool reallyExit;
        private volatile bool busy;
        private bool firstHide = true;
        private bool pendingShow;
        private bool pendingLaunch;
        private string lastPidSignature = "";
        private bool lastProxied;
        private bool lastProxiedValid;
        private DateTime suppressUntil = DateTime.MinValue;
        private DateTime lastProbeUtc = DateTime.MinValue;
        private bool lastProbeResult;
        private LocalRelay relay;
        private string relaySignature = "";
        private readonly object relayLock = new object();
        private static readonly object fileLogLock = new object();

        public MainForm(bool startHidden, bool launchNow)
        {
            startInTray = startHidden;
            launchOnStart = launchNow;
            cfg = ProxyConfig.Load();

            BuildUi();
            LoadConfigToUi();
            ApplyAutostart();
            UpdateTrayText("менеджер запущен");
        }

        private void BuildUi()
        {
            Text = "Antigravity — всегда через прокси";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(624, 682);
            Font = new Font("Segoe UI", 9F);
            Icon = LoadAppIcon();

            GroupBox grpProxy = new GroupBox();
            grpProxy.Text = "Прокси";
            grpProxy.Location = new Point(12, 12);
            grpProxy.Size = new Size(600, 168);
            Controls.Add(grpProxy);

            Label l1 = new Label();
            l1.Text = "Тип:";
            l1.Location = new Point(18, 30);
            l1.AutoSize = true;
            grpProxy.Controls.Add(l1);

            cmbType = new ComboBox();
            cmbType.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbType.Location = new Point(62, 26);
            cmbType.Size = new Size(130, 24);
            cmbType.Items.Add("HTTP");
            cmbType.Items.Add("SOCKS5");
            grpProxy.Controls.Add(cmbType);

            Label l2 = new Label();
            l2.Text = "Адрес:";
            l2.Location = new Point(206, 30);
            l2.AutoSize = true;
            grpProxy.Controls.Add(l2);

            txtHost = new TextBox();
            txtHost.Location = new Point(258, 26);
            txtHost.Size = new Size(150, 24);
            grpProxy.Controls.Add(txtHost);

            Label l3 = new Label();
            l3.Text = "Порт:";
            l3.Location = new Point(420, 30);
            l3.AutoSize = true;
            grpProxy.Controls.Add(l3);

            numPort = new NumericUpDown();
            numPort.Minimum = 1;
            numPort.Maximum = 65535;
            numPort.Location = new Point(462, 26);
            numPort.Size = new Size(80, 24);
            grpProxy.Controls.Add(numPort);

            Label l4 = new Label();
            l4.Text = "Логин:";
            l4.Location = new Point(18, 66);
            l4.AutoSize = true;
            grpProxy.Controls.Add(l4);

            txtUser = new TextBox();
            txtUser.Location = new Point(76, 62);
            txtUser.Size = new Size(140, 24);
            grpProxy.Controls.Add(txtUser);

            Label l5 = new Label();
            l5.Text = "Пароль:";
            l5.Location = new Point(232, 66);
            l5.AutoSize = true;
            grpProxy.Controls.Add(l5);

            txtPass = new TextBox();
            txtPass.Location = new Point(296, 62);
            txtPass.Size = new Size(140, 24);
            txtPass.UseSystemPasswordChar = true;
            grpProxy.Controls.Add(txtPass);

            Label note = new Label();
            note.Text = "Логин и пароль — для HTTP-прокси. Для SOCKS5 — только без авторизации.";
            note.Location = new Point(18, 96);
            note.Size = new Size(560, 18);
            note.ForeColor = Gray;
            grpProxy.Controls.Add(note);

            btnCheck = new Button();
            btnCheck.Text = "Проверить прокси";
            btnCheck.Location = new Point(18, 120);
            btnCheck.Size = new Size(150, 30);
            btnCheck.Click += delegate { CheckProxyFromUi(); };
            grpProxy.Controls.Add(btnCheck);

            lblCheck = new Label();
            lblCheck.Text = "";
            lblCheck.Location = new Point(180, 127);
            lblCheck.Size = new Size(400, 20);
            lblCheck.ForeColor = Gray;
            grpProxy.Controls.Add(lblCheck);

            GroupBox grpChat = new GroupBox();
            grpChat.Text = "Antigravity";
            grpChat.Location = new Point(12, 190);
            grpChat.Size = new Size(600, 206);
            Controls.Add(grpChat);

            Label ls = new Label();
            ls.Text = "Состояние:";
            ls.Location = new Point(18, 32);
            ls.AutoSize = true;
            grpChat.Controls.Add(ls);

            lblStatus = new Label();
            lblStatus.Text = "проверяю...";
            lblStatus.Location = new Point(100, 32);
            lblStatus.Size = new Size(480, 20);
            lblStatus.Font = new Font(Font, FontStyle.Bold);
            lblStatus.ForeColor = Gray;
            grpChat.Controls.Add(lblStatus);

            Button btnStart = new Button();
            btnStart.Text = "Запустить через прокси";
            btnStart.Location = new Point(18, 62);
            btnStart.Size = new Size(180, 36);
            btnStart.Click += delegate { LaunchAntigravity(true); };
            grpChat.Controls.Add(btnStart);

            Button btnRestart = new Button();
            btnRestart.Text = "Перезапустить";
            btnRestart.Location = new Point(206, 62);
            btnRestart.Size = new Size(120, 36);
            btnRestart.Click += delegate { LaunchAntigravity(true); };
            grpChat.Controls.Add(btnRestart);

            Button btnStop = new Button();
            btnStop.Text = "Остановить";
            btnStop.Location = new Point(334, 62);
            btnStop.Size = new Size(120, 36);
            btnStop.Click += delegate { StopAntigravity(); };
            grpChat.Controls.Add(btnStop);

            chkEnforce = new CheckBox();
            chkEnforce.Text = "Следить и всегда запускать через прокси";
            chkEnforce.Location = new Point(18, 112);
            chkEnforce.Size = new Size(300, 22);
            grpChat.Controls.Add(chkEnforce);

            chkStartAtLogon = new CheckBox();
            chkStartAtLogon.Text = "Запускать Antigravity при входе в Windows";
            chkStartAtLogon.Location = new Point(18, 138);
            chkStartAtLogon.Size = new Size(300, 22);
            grpChat.Controls.Add(chkStartAtLogon);

            chkRunAtLogon = new CheckBox();
            chkRunAtLogon.Text = "Автозапуск менеджера";
            chkRunAtLogon.Location = new Point(330, 112);
            chkRunAtLogon.Size = new Size(250, 22);
            grpChat.Controls.Add(chkRunAtLogon);

            chkMinimize = new CheckBox();
            chkMinimize.Text = "Сворачивать в трей";
            chkMinimize.Location = new Point(330, 138);
            chkMinimize.Size = new Size(250, 22);
            grpChat.Controls.Add(chkMinimize);

            chkStopOnProxyDown = new CheckBox();
            chkStopOnProxyDown.Text = "Останавливать Antigravity, если прокси недоступен";
            chkStopOnProxyDown.Location = new Point(18, 164);
            chkStopOnProxyDown.Size = new Size(420, 22);
            grpChat.Controls.Add(chkStopOnProxyDown);

            chkRunAtLogon.CheckedChanged += delegate { UpdateStartAtLogonState(); };

            GroupBox grpLog = new GroupBox();
            grpLog.Text = "Журнал";
            grpLog.Location = new Point(12, 408);
            grpLog.Size = new Size(600, 196);
            Controls.Add(grpLog);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Location = new Point(12, 22);
            txtLog.Size = new Size(576, 164);
            txtLog.Font = new Font("Consolas", 8.25F);
            txtLog.BackColor = Color.White;
            grpLog.Controls.Add(txtLog);

            Button btnSave = new Button();
            btnSave.Text = "Сохранить";
            btnSave.Location = new Point(12, 616);
            btnSave.Size = new Size(150, 34);
            btnSave.Click += delegate { SaveFromUi(true); };
            Controls.Add(btnSave);

            Button btnShortcut = new Button();
            btnShortcut.Text = "Создать ярлык";
            btnShortcut.Location = new Point(170, 616);
            btnShortcut.Size = new Size(170, 34);
            btnShortcut.Click += delegate { CreateShortcut(); };
            Controls.Add(btnShortcut);

            Button btnHide = new Button();
            btnHide.Text = "Свернуть в трей";
            btnHide.Location = new Point(348, 616);
            btnHide.Size = new Size(150, 34);
            btnHide.Click += delegate { HideToTray(); };
            Controls.Add(btnHide);

            Label footer = new Label();
            footer.Text = "Прокси применяется только к Antigravity. Системные настройки Windows не меняются.";
            footer.Location = new Point(12, 656);
            footer.Size = new Size(600, 18);
            footer.ForeColor = Gray;
            Controls.Add(footer);

            BuildTray();
            FormClosing += OnFormClosing;
        }

        private void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = Icon;
            tray.Text = "Antigravity Proxy";
            tray.Visible = true;
            tray.DoubleClick += delegate { RestoreFromTray(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Открыть настройки", null, delegate { RestoreFromTray(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Запустить Antigravity через прокси", null, delegate { LaunchAntigravity(true); });
            menu.Items.Add("Остановить Antigravity", null, delegate { StopAntigravity(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                string exe = AntigravityController.FindExe();
                if (!string.IsNullOrEmpty(exe))
                {
                    Icon i = Icon.ExtractAssociatedIcon(exe);
                    if (i != null) return i;
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            AppendLog("Менеджер запущен. Прокси: " + cfg.Url);
            AppendLog("Прокси применяется только к Antigravity, системные настройки не меняются.");

            if (startInTray)
            {
                HideToTray();
            }

            if (pendingShow)
            {
                pendingShow = false;
                RestoreFromTray();
            }
            if (pendingLaunch || launchOnStart)
            {
                pendingLaunch = false;
                LaunchAntigravity(false);
            }

            watchdog = new System.Threading.Timer(WatchdogTick, null, 5000, 3000);

            if (startInTray && cfg.StartAntigravityAtLogon)
            {
                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    Thread.Sleep(8000);
                    try
                    {
                        if (IsDisposed) return;
                        if (AntigravityController.IsRunning()) return;
                        if (!ProxyTester.TcpReachable(cfg.Host, cfg.Port, 1200))
                        {
                            AppendLog("Прокси недоступен — Antigravity при входе не запущен.");
                            return;
                        }
                        BeginInvoke(new MethodInvoker(delegate { LaunchAntigravity(false); }));
                    }
                    catch { }
                });
            }
        }

        private void LoadConfigToUi()
        {
            cmbType.SelectedIndex = cfg.Type == "SOCKS5" ? 1 : 0;
            txtHost.Text = cfg.Host;
            numPort.Value = Math.Max(numPort.Minimum, Math.Min(numPort.Maximum, cfg.Port));
            txtUser.Text = cfg.User;
            txtPass.Text = cfg.Password;
            chkEnforce.Checked = cfg.Enforce;
            chkRunAtLogon.Checked = cfg.RunAtLogon;
            chkStartAtLogon.Checked = cfg.StartAntigravityAtLogon;
            chkMinimize.Checked = cfg.MinimizeToTray;
            chkStopOnProxyDown.Checked = cfg.StopWhenProxyDown;
            UpdateStartAtLogonState();
        }

        private void UpdateStartAtLogonState()
        {
            if (!chkRunAtLogon.Checked) chkStartAtLogon.Checked = false;
            chkStartAtLogon.Enabled = chkRunAtLogon.Checked;
        }

        private ProxyConfig CollectConfig()
        {
            ProxyConfig c = new ProxyConfig();
            c.Type = cmbType.SelectedIndex == 1 ? "SOCKS5" : "HTTP";
            c.Host = txtHost.Text.Trim();
            c.Port = (int)numPort.Value;
            c.User = txtUser.Text.Trim();
            c.Password = txtPass.Text;
            c.Enforce = chkEnforce.Checked;
            c.RunAtLogon = chkRunAtLogon.Checked;
            c.StartAntigravityAtLogon = chkStartAtLogon.Checked;
            c.MinimizeToTray = chkMinimize.Checked;
            c.StopWhenProxyDown = chkStopOnProxyDown.Checked;
            return c;
        }

        private static string Validate(ProxyConfig c)
        {
            if (c.Host.Length == 0) return "Укажите адрес прокси.";
            if (c.Port <= 0 || c.Port > 65535) return "Укажите корректный порт.";
            if (c.Type == "SOCKS5" && c.HasCredentials) return "SOCKS5 с логином и паролем не поддерживается.";
            return null;
        }

        private bool SaveFromUi(bool apply)
        {
            ProxyConfig c = CollectConfig();
            string error = Validate(c);
            if (error != null)
            {
                MessageBox.Show(this, error, "Проверьте настройки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            ProxyConfig previous = cfg;
            bool proxyChanged = previous.Type != c.Type || previous.Host != c.Host ||
                previous.Port != c.Port || previous.User != c.User || previous.Password != c.Password ||
                previous.Enforce != c.Enforce;

            cfg = c;
            cfg.Save();
            ApplyAutostart();
            if (proxyChanged) ResetRelayIfNeeded();
            lastPidSignature = "";
            lastProxiedValid = false;
            AppendLog("Настройки сохранены: " + cfg.Url);

            if (apply)
            {
                chkEnforce.Checked = cfg.Enforce;
                if (proxyChanged && cfg.Enforce && AntigravityController.IsRunning())
                {
                    if (ProxyTester.TcpReachable(cfg.Host, cfg.Port, 900))
                    {
                        LaunchAntigravity(false);
                    }
                    else
                    {
                        AppendLog("Прокси недоступен — работающий Antigravity пока не перезапускаю.");
                    }
                }
            }
            return true;
        }

        private void ApplyAutostart()
        {
            try
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (run == null) return;
                    if (cfg.RunAtLogon)
                    {
                        run.SetValue("AntigravityProxyManager",
                            "\"" + Application.ExecutablePath + "\" --tray");
                    }
                    else
                    {
                        run.DeleteValue("AntigravityProxyManager", false);
                    }
                }
            }
            catch { }
        }

        private void CheckProxyFromUi()
        {
            ProxyConfig c = CollectConfig();
            string error = Validate(c);
            if (error != null)
            {
                lblCheck.Text = error;
                lblCheck.ForeColor = Red;
                return;
            }

            btnCheck.Enabled = false;
            lblCheck.Text = "Проверяю...";
            lblCheck.ForeColor = Gray;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                string result = ProxyTester.Check(c);
                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        btnCheck.Enabled = true;
                        if (result == null)
                        {
                            lblCheck.Text = "Прокси работает";
                            lblCheck.ForeColor = Green;
                            AppendLog("Проверка прокси " + c.Url + ": успешно");
                        }
                        else
                        {
                            lblCheck.Text = "Ошибка: " + result;
                            lblCheck.ForeColor = Red;
                            AppendLog("Проверка прокси " + c.Url + ": " + result);
                        }
                    }));
                }
                catch { }
            });
        }

        private void LaunchAntigravity(bool fromUi)
        {
            if (fromUi && !SaveFromUi(false)) return;
            if (busy) return;
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate(object state) { LaunchWorker(); });
        }

        private void LaunchWorker()
        {
            try
            {
                string proxyError = ProxyTester.Check(cfg);
                if (proxyError != null)
                {
                    LogAsync("Прокси " + cfg.Url + " не работает: " + proxyError);
                    SetStatusAsync("Прокси недоступен", Orange);
                    BalloonAsync("Прокси недоступен", "Antigravity не запущен. " + proxyError);
                    return;
                }

                string url;
                bool relayUsed;
                if (!EnsureProxyUrl(out url, out relayUsed)) return;

                List<AppProc> processes;
                if (AntigravityProcesses.TryGetAll(out processes) && processes.Count > 0)
                {
                    AppProc main = AntigravityProcesses.FindMain(processes);
                    string needle = "--proxy-server=" + url;
                    if (main != null && main.CommandLine.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        AntigravityController.ActivateWindow();
                        LogAsync("Antigravity уже работает через этот прокси.");
                        SetStatusAsync("Antigravity работает через прокси", Green);
                        return;
                    }
                }

                int killed = AntigravityController.KillAll();
                if (killed > 0) LogAsync("Остановлено процессов Antigravity: " + killed.ToString());
                Thread.Sleep(700);

                Process p = AntigravityController.Launch(cfg, url);
                suppressUntil = DateTime.UtcNow.AddSeconds(20);
                lastPidSignature = "";
                lastProxiedValid = false;

                AppendLog("Antigravity запущен через " + url + (relayUsed ? " (локальный ретранслятор)" : ""));
                if (p != null) AppendLog("PID: " + p.Id.ToString());
                SetStatusAsync("Antigravity работает через прокси", Green);
            }
            catch (Exception ex)
            {
                LogAsync("Ошибка запуска: " + ex.Message);
                SetStatusAsync("Ошибка запуска", Red);
            }
            finally
            {
                busy = false;
            }
        }

        private bool EnsureProxyUrl(out string url, out bool relayUsed)
        {
            url = cfg.Url;
            relayUsed = false;
            lock (relayLock)
            {
                try
                {
                    if (cfg.NeedsRelay)
                    {
                        string signature = cfg.Host + "|" + cfg.Port.ToString() + "|" + cfg.User + "|" + cfg.Password;
                        if (relay == null || relaySignature != signature)
                        {
                            if (relay != null)
                            {
                                relay.Stop();
                                relay = null;
                            }
                            relay = new LocalRelay(cfg.Host, cfg.Port, cfg.User, cfg.Password);
                            relay.Start();
                            relaySignature = signature;
                            AppendLog("Локальный ретранслятор запущен на 127.0.0.1:" + relay.Port.ToString());
                        }
                        url = "http://127.0.0.1:" + relay.Port.ToString();
                        relayUsed = true;
                    }
                    else
                    {
                        if (relay != null)
                        {
                            relay.Stop();
                            relay = null;
                            relaySignature = "";
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    LogAsync("Не удалось запустить локальный ретранслятор: " + ex.Message);
                    SetStatusAsync("Ошибка ретранслятора", Red);
                    return false;
                }
            }
        }

        private void ResetRelayIfNeeded()
        {
            lock (relayLock)
            {
                if (relay != null)
                {
                    relay.Stop();
                    relay = null;
                    relaySignature = "";
                }
            }
        }

        private void StopAntigravity()
        {
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try
                {
                    int killed = AntigravityController.KillAll();
                    AppendLog("Antigravity остановлен (процессов: " + killed.ToString() + ").");
                    SetStatusAsync("Antigravity не запущен", Gray);
                    lastPidSignature = "";
                    lastProxiedValid = false;
                }
                catch (Exception ex)
                {
                    LogAsync("Не удалось остановить Antigravity: " + ex.Message);
                }
            });
        }

        private void WatchdogTick(object state)
        {
            if (!cfg.Enforce || reallyExit || IsDisposed) return;
            if (Interlocked.CompareExchange(ref watchdogBusyMarker, 1, 0) != 0) return;
            try
            {
                if (busy) return;
                if (DateTime.UtcNow < suppressUntil) return;

                if (!IsProxyReachable())
                {
                    SetStatusAsync("Прокси недоступен", Orange);
                    if (cfg.StopWhenProxyDown) StopDirectAntigravityIfNeeded();
                    return;
                }

                List<AppProc> processes;
                if (!AntigravityProcesses.TryGetAll(out processes))
                {
                    SetStatusAsync("Не удалось проверить процессы", Orange);
                    return;
                }

                if (processes.Count == 0)
                {
                    SetStatusAsync("Antigravity не запущен", Gray);
                    lastPidSignature = "";
                    lastProxiedValid = false;
                    return;
                }

                string signature = BuildSignature(processes);
                bool proxied;
                if (signature == lastPidSignature && lastProxiedValid)
                {
                    proxied = lastProxied;
                }
                else
                {
                    string url;
                    bool relayUsed;
                    if (!EnsureProxyUrl(out url, out relayUsed)) return;
                    AppProc main = AntigravityProcesses.FindMain(processes);
                    proxied = main != null &&
                        main.CommandLine.IndexOf("--proxy-server=" + url, StringComparison.OrdinalIgnoreCase) >= 0;
                    lastPidSignature = signature;
                    lastProxied = proxied;
                    lastProxiedValid = true;
                }

                if (proxied)
                {
                    SetStatusAsync("Antigravity работает через прокси", Green);
                    return;
                }

                AppendLog("Antigravity запущен без прокси — перезапускаю через прокси...");
                string targetUrl;
                bool used;
                if (!EnsureProxyUrl(out targetUrl, out used)) return;

                AntigravityController.KillAll();
                Thread.Sleep(700);
                AntigravityController.Launch(cfg, targetUrl);
                suppressUntil = DateTime.UtcNow.AddSeconds(20);
                lastPidSignature = "";
                lastProxiedValid = false;
                AppendLog("Antigravity перезапущен через " + targetUrl);
                SetStatusAsync("Antigravity работает через прокси", Green);
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref watchdogBusyMarker, 0);
            }
        }

        private int watchdogBusyMarker;

        private void StopDirectAntigravityIfNeeded()
        {
            List<AppProc> list;
            if (!AntigravityProcesses.TryGetAll(out list) || list.Count == 0) return;

            string url;
            bool relayUsed;
            if (!EnsureProxyUrl(out url, out relayUsed)) return;

            AppProc main = AntigravityProcesses.FindMain(list);
            if (main != null && main.CommandLine.IndexOf("--proxy-server=" + url,
                StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            AppendLog("Прокси недоступен, а Antigravity запущен без прокси — останавливаю Antigravity.");
            AntigravityController.KillAll();
            SetStatusAsync("Прокси недоступен — Antigravity остановлен", Orange);
            lastPidSignature = "";
            lastProxiedValid = false;
        }

        private static string BuildSignature(List<AppProc> list)
        {
            List<int> ids = new List<int>();
            foreach (AppProc p in list) ids.Add(p.Pid);
            ids.Sort();
            StringBuilder sb = new StringBuilder();
            foreach (int id in ids) sb.Append(id.ToString()).Append(';');
            return sb.ToString();
        }

        private bool IsProxyReachable()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastProbeUtc).TotalSeconds < 4) return lastProbeResult;
            lastProbeUtc = now;
            lastProbeResult = ProxyTester.TcpReachable(cfg.Host, cfg.Port, 900);
            return lastProbeResult;
        }

        private void CreateShortcut()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string path = Path.Combine(desktop, "Antigravity (через прокси).lnk");

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { path });

                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut,
                    new object[] { Application.ExecutablePath });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut,
                    new object[] { "--launch" });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                    new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                    new object[] { "Запуск Antigravity через указанный прокси" });

                string chatExe = AntigravityController.FindExe();
                if (!string.IsNullOrEmpty(chatExe))
                {
                    shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                        new object[] { chatExe + ",0" });
                }
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

                AppendLog("Ярлык создан: " + path);
                MessageBox.Show(this, "Ярлык создан на рабочем столе:\r\n" + path,
                    "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось создать ярлык: " + ex.Message,
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void HideToTray()
        {
            if (WindowState == FormWindowState.Minimized && !Visible) return;
            ShowInTaskbar = false;
            Hide();
            if (firstHide)
            {
                firstHide = false;
                try
                {
                    tray.ShowBalloonTip(2500, "Antigravity Proxy",
                        "Менеджер продолжает работать в трее и следит за Antigravity.",
                        ToolTipIcon.Info);
                }
                catch { }
            }
        }

        private void RestoreFromTray()
        {
            if (IsDisposed) return;
            try
            {
                ShowInTaskbar = true;
                Show();
                WindowState = FormWindowState.Normal;
                if (!IsOnAnyScreen())
                {
                    Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                    Location = new Point(
                        wa.Left + Math.Max(0, (wa.Width - Width) / 2),
                        wa.Top + Math.Max(0, (wa.Height - Height) / 2));
                }
                Activate();
                BringToFront();
            }
            catch { }
        }

        private bool IsOnAnyScreen()
        {
            try
            {
                Rectangle b = Bounds;
                foreach (Screen s in Screen.AllScreens)
                {
                    if (s.WorkingArea.IntersectsWith(b)) return true;
                }
            }
            catch { }
            return false;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!reallyExit && cfg.MinimizeToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            Cleanup();
        }

        private void ExitApp()
        {
            try
            {
                if (relay != null && AntigravityController.IsUsingRelay(relay.Port))
                {
                    DialogResult answer = MessageBox.Show(this,
                        "Antigravity работает через встроенный ретранслятор менеджера.\r\n" +
                        "Если выйти, Antigravity будет остановлен (иначе он останется без доступа к сети).\r\n\r\nВыйти?",
                        "Выход", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes) return;
                }
            }
            catch { }

            reallyExit = true;
            Close();
            Application.Exit();
        }

        private void Cleanup()
        {
            try { if (watchdog != null) watchdog.Dispose(); } catch { }
            try
            {
                lock (relayLock)
                {
                    if (relay != null)
                    {
                        if (AntigravityController.IsUsingRelay(relay.Port))
                        {
                            AntigravityController.KillAll();
                        }
                        relay.Stop();
                        relay = null;
                    }
                }
            }
            catch { }
            try { tray.Visible = false; tray.Dispose(); } catch { }
        }

        public void ActivateFromExternal(bool launch)
        {
            if (IsDisposed) return;
            try
            {
                if (!IsHandleCreated)
                {
                    if (launch) pendingLaunch = true;
                    else pendingShow = true;
                    return;
                }
                BeginInvoke(new MethodInvoker(delegate()
                {
                    if (launch) LaunchAntigravity(true);
                    else RestoreFromTray();
                }));
            }
            catch { }
        }

        private void AppendLog(string message)
        {
            bool wrote = false;
            try
            {
                if (IsDisposed)
                {
                    WriteFileLog(message);
                    return;
                }
                if (InvokeRequired)
                {
                    BeginInvoke(new MethodInvoker(delegate() { AppendLog(message); }));
                    return;
                }
                WriteFileLog(message);
                wrote = true;
                if (txtLog.TextLength > 60000) txtLog.Clear();
                txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch
            {
                if (!wrote) WriteFileLog(message);
            }
        }

        private static void WriteFileLog(string message)
        {
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "manager.log");
                lock (fileLogLock)
                {
                    FileInfo info = new FileInfo(path);
                    if (info.Exists && info.Length > 1024 * 1024)
                    {
                        string old = path + ".old";
                        try { if (File.Exists(old)) File.Delete(old); } catch { }
                        try { File.Move(path, old); } catch { }
                    }
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }

        private void LogAsync(string message)
        {
            AppendLog(message);
        }

        private void SetStatusAsync(string text, Color color)
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke(new MethodInvoker(delegate()
                {
                    lblStatus.Text = text;
                    lblStatus.ForeColor = color;
                    UpdateTrayText(text);
                }));
            }
            catch { }
        }

        private void UpdateTrayText(string status)
        {
            try
            {
                string t = "Antigravity Proxy — " + status;
                if (t.Length > 63) t = t.Substring(0, 63);
                tray.Text = t;
            }
            catch { }
        }

        private void BalloonAsync(string title, string text)
        {
            try
            {
                BeginInvoke(new MethodInvoker(delegate()
                {
                    try { tray.ShowBalloonTip(4000, title, text, ToolTipIcon.Warning); }
                    catch { }
                }));
            }
            catch { }
        }
    }
}
