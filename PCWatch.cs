// ============================================================================
//  PC WATCH - Remote PC management system (single source file)
//  Designed by MD
//
//  ONE source file, TWO roles (chosen at compile time):
//    * Compiled normally            -> "setup.exe"   : installer + builder
//    * Compiled by setup.exe with
//      /define:DASHBOARD_BUILD      -> "PCWatch.exe" : dashboard + background host
//
//  PCWatch.exe also runs as the hidden background host ("--background") that owns
//  the Python server and ngrok processes, and as the uninstaller ("--uninstall").
//
//  NOTE: the compiler that ships with Windows (csc.exe v4.0.30319) only supports
//  C# 5. This file deliberately uses C# 5 syntax only and is pure ASCII.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.7.2", FrameworkDisplayName = ".NET Framework 4.7.2")]
[assembly: AssemblyTitle("PC Watch")]
[assembly: AssemblyProduct("PC Watch")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace PCWatch
{
    // ------------------------------------------------------------------------
    //  Entry point: picks the role from the command line / build flavour
    // ------------------------------------------------------------------------
    internal static class Program
    {
        public const string AppName = "PC Watch";
        public const string Version = "1.0.0";
#if DASHBOARD_BUILD
        public const bool IsDashboardBuild = true;
#else
        public const bool IsDashboardBuild = false;
#endif

        [STAThread]
        private static int Main(string[] args)
        {
            string a0 = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (a0 == "--selftest") return 42;   // used by setup.exe to verify the generated PCWatch.exe runs
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { } // TLS 1.2
            try
            {
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Paths.Init(AppDomain.CurrentDomain.BaseDirectory);
                if (a0 == "--background") return HostApp.Run(args);
                if (a0 == "--uninstall") return Uninstaller.Run(args);
                if (a0 == "--repair") return SetupApp.Run(args, true);
                if (IsDashboardBuild) return DashboardApp.Run(args);
                return SetupApp.Run(args, false);
            }
            catch (Exception ex)
            {
                Log.Write("ERROR", "Application", "Fatal error: " + ex);
                MessageBox.Show("PC Watch hit an unexpected error and must close.\r\n\r\n" + ex.Message +
                    "\r\n\r\nDetails were written to the Application log.", "PC Watch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    // ------------------------------------------------------------------------
    //  Folder layout
    // ------------------------------------------------------------------------
    internal static class Paths
    {
        public static string InstallDir = "";
        public static void Init(string dir) { InstallDir = Path.GetFullPath(dir).TrimEnd('\\', '/'); }
        public static string Sub(string n) { return Path.Combine(InstallDir, n); }
        public static string ConfigDir { get { return Sub("Config"); } }
        public static string RunDir { get { return Path.Combine(ConfigDir, "run"); } }
        public static string ServerDir { get { return Sub("Server"); } }
        public static string ToolsDir { get { return Sub("Tools"); } }
        public static string RuntimeDir { get { return Sub("Runtime"); } }
        public static string SourceDir { get { return Sub("Source"); } }
        public static string ExePath { get { return Sub("PCWatch.exe"); } }
        public static string SettingsFile { get { return Path.Combine(ConfigDir, "settings.ini"); } }
        public static string TokenFile { get { return Path.Combine(ConfigDir, "ngrok.token.dat"); } }
        public static string KeyFile { get { return Path.Combine(ConfigDir, "localkey.dat"); } }
        public static string DefaultLogDir { get { return Sub("Logs"); } }
    }

    // ------------------------------------------------------------------------
    //  Tiny key=value file format shared with the Python server
    // ------------------------------------------------------------------------
    internal static class Ini
    {
        public static string ReadText(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true)) return sr.ReadToEnd();
        }

        public static Dictionary<string, string> Read(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return d;
                foreach (string raw in ReadText(path).Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return d;
        }

        public static void Write(string path, IDictionary<string, string> d)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var sb = new StringBuilder();
            foreach (var kv in d)
                sb.Append(kv.Key).Append('=').Append((kv.Value ?? "").Replace("\r", " ").Replace("\n", " ")).Append("\r\n");
            string tmp = path + ".tmp";
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    File.Copy(tmp, path, true);
                    try { File.Delete(tmp); } catch { }
                    return;
                }
                catch (IOException) { Thread.Sleep(100); }
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }

    // ------------------------------------------------------------------------
    //  Settings (Config\settings.ini) - read by both C# and Python
    // ------------------------------------------------------------------------
    internal static class Settings
    {
        static readonly object L = new object();
        static Dictionary<string, string> cache;
        static DateTime cacheAt = DateTime.MinValue;

        public static Dictionary<string, string> Defaults()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            d["server_port"] = "8765";
            d["startup_mode"] = "0";          // 0 disabled, 1 server at logon, 2 server + tunnel at logon
            d["stop_on_exit"] = "0";          // stop server/tunnel when the dashboard closes
            d["log_level"] = "INFO";
            d["log_retention_days"] = "14";
            d["log_dir"] = "";
            d["python_path"] = "";
            d["ngrok_path"] = "";
            d["ngrok_domain"] = "";
            d["tunnel_port"] = "0";           // 0 = follow server port
            d["theme"] = "Dark";
            d["allow_screen"] = "0";
            d["allow_camera"] = "0";
            d["session_minutes"] = "30";
            d["remote_user"] = "";
            d["local_key_sha256"] = "";
            d["version"] = Program.Version;
            return d;
        }

        public static Dictionary<string, string> All()
        {
            lock (L)
            {
                if (cache == null || (DateTime.UtcNow - cacheAt).TotalSeconds > 1)
                {
                    var d = Defaults();
                    foreach (var kv in Ini.Read(Paths.SettingsFile)) d[kv.Key] = kv.Value;
                    cache = d; cacheAt = DateTime.UtcNow;
                }
                return cache;
            }
        }
        public static string Get(string k) { string v; return All().TryGetValue(k, out v) ? v : ""; }
        public static int GetInt(string k, int def) { int n; return int.TryParse(Get(k), out n) ? n : def; }
        public static bool GetBool(string k) { return Get(k) == "1"; }

        public static void Save(IDictionary<string, string> changes)
        {
            lock (L)
            {
                var d = Defaults();
                foreach (var kv in Ini.Read(Paths.SettingsFile)) d[kv.Key] = kv.Value;
                foreach (var kv in changes) d[kv.Key] = kv.Value;
                Ini.Write(Paths.SettingsFile, d);
                cache = null;
            }
        }
        public static void Set(string k, string v) { var d = new Dictionary<string, string>(); d[k] = v; Save(d); }
    }

    // ------------------------------------------------------------------------
    //  Logging: Logs\*.log, format "time|LEVEL|Category|message", rotation + retention
    // ------------------------------------------------------------------------
    internal static class Log
    {
        static readonly object L = new object();
        static readonly List<string> secrets = new List<string>();
        static DateTime lastClean = DateTime.MinValue;
        public static string OverrideDir;   // used by the installer before settings exist
        static readonly Regex Rx = new Regex(@"(?i)\b(authtoken|token|password|passwd|secret|cookie|authorization)\b(""?\s*[:=]\s*""?)[^\s,;""]+", RegexOptions.Compiled);

        public static string[] AllFiles = { "application.log", "server.log", "server-process.log", "ngrok.log", "install.log", "auth.log", "connections.log" };

        public static string Dir
        {
            get
            {
                if (!string.IsNullOrEmpty(OverrideDir)) return OverrideDir;
                string d = Settings.Get("log_dir");
                return string.IsNullOrWhiteSpace(d) ? Paths.DefaultLogDir : d;
            }
        }
        public static string FileFor(string cat)
        {
            switch ((cat ?? "").ToLowerInvariant())
            {
                case "server": return "server.log";
                case "serverproc": return "server-process.log";
                case "ngrok": return "ngrok.log";
                case "installation": return "install.log";
                case "authentication": return "auth.log";
                case "connections": return "connections.log";
                default: return "application.log";
            }
        }
        static string CatName(string cat)
        {
            switch ((cat ?? "").ToLowerInvariant())
            {
                case "serverproc": return "Server";
                case "server": return "Server";
                case "ngrok": return "ngrok";
                case "installation": return "Installation";
                case "authentication": return "Authentication";
                case "connections": return "Connections";
                default: return "Application";
            }
        }
        static int Num(string lv)
        {
            switch (lv) { case "DEBUG": return 10; case "WARNING": return 30; case "ERROR": return 40; case "CRITICAL": return 50; default: return 20; }
        }
        public static void AddSecret(string s) { if (!string.IsNullOrEmpty(s) && s.Length >= 6) lock (L) { if (!secrets.Contains(s)) secrets.Add(s); } }
        public static string Redact(string m)
        {
            lock (L) { foreach (string s in secrets) m = m.Replace(s, "***"); }
            return Rx.Replace(m, "$1$2***");
        }

        public static void Info(string cat, string m) { Write("INFO", cat, m); }
        public static void Warn(string cat, string m) { Write("WARNING", cat, m); }
        public static void Error(string cat, string m) { Write("ERROR", cat, m); }

        public static void Write(string level, string cat, string msg)
        {
            try
            {
                string lvl = level.ToUpperInvariant();
                if (Num(lvl) < Num(Settings.Get("log_level").ToUpperInvariant())) return;
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "|" + lvl + "|" + CatName(cat) + "|" +
                    Redact(msg ?? "").Replace("\r", " ").Replace("\n", " ") + "\r\n";
                lock (L)
                {
                    string dir = Dir;
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, FileFor(cat));
                    Rotate(path);
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                    Cleanup(dir);
                }
            }
            catch { }
        }

        static void Rotate(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < 2 * 1024 * 1024) return;
                for (int i = 4; i >= 1; i--)
                {
                    string from = path + "." + i, to = path + "." + (i + 1);
                    if (File.Exists(to)) File.Delete(to);
                    if (File.Exists(from)) File.Move(from, to);
                }
                File.Move(path, path + ".1");
            }
            catch { }
        }

        static void Cleanup(string dir)
        {
            if ((DateTime.UtcNow - lastClean).TotalMinutes < 60) return;
            lastClean = DateTime.UtcNow;
            try
            {
                int days = Math.Max(1, Settings.GetInt("log_retention_days", 14));
                foreach (string f in Directory.GetFiles(dir, "*.log.*"))
                    if ((DateTime.Now - File.GetLastWriteTime(f)).TotalDays > days) File.Delete(f);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------------
    //  Secret storage: Windows DPAPI, bound to the current Windows user
    // ------------------------------------------------------------------------
    internal static class Secrets
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PCWatch.v1");
        public static void Save(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
        }
        public static string Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
            }
            catch { return null; }
        }
        public static string RandomHex(int bytes)
        {
            var b = new byte[bytes];
            using (var r = new RNGCryptoServiceProvider()) r.GetBytes(b);
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
        public static string Sha256Hex(string s)
        {
            using (var h = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (byte x in h.ComputeHash(Encoding.UTF8.GetBytes(s))) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }
    }

    // ------------------------------------------------------------------------
    //  Password policy (the Python server enforces the same rules)
    // ------------------------------------------------------------------------
    internal static class Policy
    {
        public static string CheckUser(string u)
        {
            if (u == null || !Regex.IsMatch(u, @"^[A-Za-z0-9_.-]{3,32}$")) return "Username must be 3-32 characters: letters, digits, '.', '_' or '-'.";
            return null;
        }
        public static string CheckPassword(string p, string user)
        {
            if (p == null || p.Length < 12) return "Password must be at least 12 characters.";
            if (!Regex.IsMatch(p, "[a-z]") || !Regex.IsMatch(p, "[A-Z]") || !Regex.IsMatch(p, "[0-9]") || !Regex.IsMatch(p, "[^A-Za-z0-9]"))
                return "Password needs lowercase, uppercase, a digit and a symbol.";
            if (!string.IsNullOrEmpty(user) && p.IndexOf(user, StringComparison.OrdinalIgnoreCase) >= 0) return "Password must not contain the username.";
            return null;
        }
    }

    // ------------------------------------------------------------------------
    //  Win32 helpers
    // ------------------------------------------------------------------------
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMSTAT
        {
            public uint dwLength; public uint dwMemoryLoad;
            public ulong ullTotalPhys; public ulong ullAvailPhys; public ulong ullTotalPageFile; public ulong ullAvailPageFile;
            public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
        }
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMSTAT m);
        [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    }

    // ------------------------------------------------------------------------
    //  Real system information (no dummy values)
    // ------------------------------------------------------------------------
    internal static class SysInfo
    {
        static long pi, pk, pu; static bool have; static readonly object L = new object();

        public static double Cpu()
        {
            lock (L)
            {
                long i, k, u;
                if (!Native.GetSystemTimes(out i, out k, out u)) return 0;
                double r = 0;
                if (have) { long tot = (k - pk) + (u - pu); if (tot > 0) r = (tot - (i - pi)) * 100.0 / tot; }
                pi = i; pk = k; pu = u; have = true;
                return Math.Max(0, Math.Min(100, r));
            }
        }
        public static void Ram(out ulong total, out ulong avail)
        {
            var m = new Native.MEMSTAT(); m.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMSTAT));
            if (Native.GlobalMemoryStatusEx(ref m)) { total = m.ullTotalPhys; avail = m.ullAvailPhys; } else { total = 0; avail = 0; }
        }
        public static TimeSpan Uptime() { return TimeSpan.FromMilliseconds((double)Native.GetTickCount64()); }
        static string Reg(string sub, string name)
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(sub)) return k == null ? "" : Convert.ToString(k.GetValue(name)); } catch { return ""; }
        }
        public static string OsName()
        {
            string n = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName"); int b;
            int.TryParse(Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber"), out b);
            if (b >= 22000) n = n.Replace("Windows 10", "Windows 11");
            return string.IsNullOrEmpty(n) ? Environment.OSVersion.ToString() : n;
        }
        public static string OsVersion()
        {
            string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            string dv = Reg(key, "DisplayVersion"); if (dv == "") dv = Reg(key, "ReleaseId");
            return (dv + " (build " + Reg(key, "CurrentBuildNumber") + "." + Reg(key, "UBR") + ")").Trim();
        }
        public static string Cpu0Name() { return Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString").Trim(); }
        public static string Arch() { return Environment.Is64BitOperatingSystem ? "64-bit (x64)" : "32-bit (x86)"; }
        public static List<string> LocalIPs()
        {
            var l = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) l.Add(ua.Address.ToString());
                }
            }
            catch { }
            return l;
        }
        public static string Gb(ulong bytes) { return (bytes / 1073741824.0).ToString("0.0") + " GB"; }
        public static string Span(TimeSpan t)
        {
            if (t.TotalDays >= 1) return (int)t.TotalDays + "d " + t.Hours + "h " + t.Minutes + "m";
            if (t.TotalHours >= 1) return t.Hours + "h " + t.Minutes + "m " + t.Seconds + "s";
            return t.Minutes + "m " + t.Seconds + "s";
        }
        public static bool IsAdmin() { using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
    }

    // ------------------------------------------------------------------------
    //  Process helpers (identify our own processes by full image path)
    // ------------------------------------------------------------------------
    internal static class ProcTools
    {
        public static string PathOf(int pid)
        {
            IntPtr h = Native.OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try { var sb = new StringBuilder(1024); int n = sb.Capacity; return Native.QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null; }
            finally { Native.CloseHandle(h); }
        }
        public static bool SamePath(string a, string b)
        {
            try { return a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }
        public static Process Get(int pid)
        {
            try { Process p = Process.GetProcessById(pid); return p.HasExited ? null : p; } catch { return null; }
        }
        public static void KillTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F") { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) p.WaitForExit(8000);
            }
            catch { }
        }
        // All processes whose executable lives inside the given folder
        public static List<Process> InDir(string dir)
        {
            var res = new List<Process>();
            string prefix = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try { if (p.Id == me) continue; string path = PathOf(p.Id); if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) res.Add(p); } catch { }
            }
            return res;
        }

        // Runs a program, streaming every output line to onLine. Returns the exit code (-1 timeout, -2 failed to start).
        public static int RunStream(string file, string args, int timeoutMs, Action<string> onLine, Dictionary<string, string> env, string stdin, string workDir)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = stdin != null, WorkingDirectory = workDir ?? Environment.CurrentDirectory
            };
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            using (var p = new Process { StartInfo = psi })
            {
                var lk = new object();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (lk) { if (onLine != null) onLine(e.Data); } };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (lk) { if (onLine != null) onLine(e.Data); } };
                try { p.Start(); } catch (Exception ex) { if (onLine != null) onLine(ex.Message); return -2; }
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (stdin != null) { try { p.StandardInput.Write(stdin); p.StandardInput.Close(); } catch { } }
                if (!p.WaitForExit(timeoutMs)) { KillTree(p.Id); return -1; }
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        public static int Run(string file, string args, int timeoutMs, out string output)
        {
            return Run(file, args, timeoutMs, out output, null, null, null);
        }
        public static int Run(string file, string args, int timeoutMs, out string output, Dictionary<string, string> env, string stdin, string workDir)
        {
            var sb = new StringBuilder();
            int code = RunStream(file, args, timeoutMs, delegate(string l) { sb.AppendLine(l); }, env, stdin, workDir);
            output = sb.ToString();
            return code;
        }
    }

    internal static class PortTools
    {
        public static bool InUse(int port)
        {
            try { foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()) if (ep.Port == port) return true; } catch { }
            return false;
        }
    }

    // ------------------------------------------------------------------------
    //  HTTP + JSON helpers
    // ------------------------------------------------------------------------
    internal static class Http
    {
        // Returns the HTTP status (0 = no response; text then holds the error message)
        public static int Send(string method, string url, string body, Dictionary<string, string> headers, int timeoutMs, bool noProxy, out string text)
        {
            text = "";
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method; req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "PCWatch/" + Program.Version;
                if (noProxy) req.Proxy = null;
                if (headers != null) foreach (var kv in headers) req.Headers[kv.Key] = kv.Value;
                if (method == "POST" && body == null) body = "{}";
                if (body != null)
                {
                    byte[] b = Encoding.UTF8.GetBytes(body);
                    req.ContentType = "application/json"; req.ContentLength = b.Length;
                    using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    text = sr.ReadToEnd();
                    return (int)resp.StatusCode;
                }
            }
            catch (WebException wex)
            {
                var r = wex.Response as HttpWebResponse;
                if (r != null)
                {
                    try { using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd(); } catch { }
                    int c = (int)r.StatusCode; r.Close(); return c;
                }
                text = wex.Message; return 0;
            }
            catch (Exception ex) { text = ex.Message; return 0; }
        }
    }

    internal static class Json
    {
        static readonly JavaScriptSerializer S = new JavaScriptSerializer();
        public static Dictionary<string, object> Obj(string text)
        {
            try { return S.DeserializeObject(text) as Dictionary<string, object>; } catch { return null; }
        }
        public static string Str(Dictionary<string, object> d, string k)
        {
            object v; return (d != null && d.TryGetValue(k, out v) && v != null) ? Convert.ToString(v) : "";
        }
        public static string Serialize(object o) { return S.Serialize(o); }
    }

    // ------------------------------------------------------------------------
    //  Authenticated loopback channel between PCWatch.exe and the Python server
    //  (a random 256-bit key; only its SHA-256 is stored in settings.ini)
    // ------------------------------------------------------------------------
    internal static class LocalApi
    {
        static string key;
        public static string BaseUrl { get { return "http://127.0.0.1:" + Settings.GetInt("server_port", 8765); } }
        static Dictionary<string, string> Hdr()
        {
            if (key == null) key = Secrets.Load(Paths.KeyFile) ?? "";
            var h = new Dictionary<string, string>(); h["X-PCWatch-Local-Key"] = key; return h;
        }
        public static bool Health()
        {
            string t; int c = Http.Send("GET", BaseUrl + "/api/health", null, null, 1500, true, out t);
            return c == 200 && t.Contains("pc-watch");
        }
        public static Dictionary<string, object> Call(string method, string path, string body, int timeoutMs, out int status)
        {
            string t; status = Http.Send(method, BaseUrl + path, body, Hdr(), timeoutMs, true, out t);
            return status == 200 ? Json.Obj(t) : null;
        }
        public static Dictionary<string, object> Get(string path) { int s; return Call("GET", path, null, 2500, out s); }
        public static bool Post(string path, string body) { int s; Call("POST", path, body, 4000, out s); return s == 200; }
    }

    // ------------------------------------------------------------------------
    //  Desired state (written by the dashboard / tray / remote UI, executed by the host)
    // ------------------------------------------------------------------------
    internal static class Desired
    {
        static readonly object L = new object();
        public static string FilePath { get { return Path.Combine(Paths.RunDir, "desired.ini"); } }
        public static Dictionary<string, string> Load() { return Ini.Read(FilePath); }
        public static bool Flag(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) && v == "1"; }
        public static long Num(Dictionary<string, string> d, string k) { string v; long n; return (d.TryGetValue(k, out v) && long.TryParse(v, out n)) ? n : 0; }
        public static void Set(params string[] kv)
        {
            lock (L)
            {
                Directory.CreateDirectory(Paths.RunDir);
                var d = Load();
                for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
                Ini.Write(FilePath, d);
            }
        }
        static string Stamp() { return DateTime.UtcNow.Ticks.ToString(); }
        public static void StartServer() { Set("server", "1", "restart_server", Stamp()); }
        public static void StopServer() { Set("server", "0"); }
        public static void StartTunnel() { Set("tunnel", "1", "restart_tunnel", Stamp()); }
        public static void StopTunnel() { Set("tunnel", "0"); }
        public static void StartBoth() { Set("server", "1", "tunnel", "1", "restart_server", Stamp(), "restart_tunnel", Stamp()); }
        public static void StopBoth() { Set("server", "0", "tunnel", "0"); }
        public static void ApplyStartupMode()
        {
            int m = Settings.GetInt("startup_mode", 0);
            if (m >= 1) Set("server", "1", "tunnel", m >= 2 ? "1" : "0");
        }
    }

    // Status published by the host once per second; read by the dashboard and the Python server
    internal static class StatusFile
    {
        public static string FilePath { get { return Path.Combine(Paths.RunDir, "status.ini"); } }
        public static long UnixNow() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
        public static DateTime FromUnix(long s) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s); }
    }

    // ========================================================================
    //  BACKGROUND HOST: owns the Python server and ngrok processes.
    //  Runs as "PCWatch.exe --background" (no window, tray icon only).
    // ========================================================================
    internal abstract class ManagedProcess
    {
        public string Name;
        public volatile string State = "Stopped";
        public string Error = "";
        public int Pid;
        public DateTime StartedUtc = DateTime.MinValue;
        protected Process P;
        protected DateTime startedAt, nextRetry = DateTime.MinValue;
        protected List<DateTime> crashes = new List<DateTime>();
        protected int backoff = 2;
        protected volatile string lastErr = "";

        protected abstract string Cat { get; }
        protected abstract string ExpectedExe { get; }
        protected abstract bool Prepare(out string error, out ProcessStartInfo psi);
        protected abstract void Evaluate();
        protected virtual void GracefulStop() { }
        protected virtual void OnLine(string line, bool isErr) { }
        protected virtual string FatalMessage(int code) { return null; }
        protected virtual string LineLevel(string line, bool isErr) { return isErr ? "ERROR" : "INFO"; }
        string PidFile { get { return Path.Combine(Paths.RunDir, Name + ".pid"); } }

        protected void SetState(string s, string err)
        {
            if (State != s || Error != err)
            {
                if (State != s) Log.Info("Application", Name + " state: " + State + " -> " + s + (string.IsNullOrEmpty(err) ? "" : " (" + err + ")"));
                State = s; Error = err;
            }
        }

        public void Tick(bool want, bool restart)
        {
            if (restart) { Stop(); crashes.Clear(); backoff = 2; SetState("Stopped", ""); }
            if (!want)
            {
                if (P != null) Stop();
                if (State != "Stopped") SetState("Stopped", "");
                return;
            }
            switch (State)
            {
                case "Stopped": if (!TryAdopt()) Start(); break;
                case "Restarting": if (DateTime.UtcNow >= nextRetry) Start(); break;
                case "Error": break;   // waits for an explicit Start/Restart from the user
                default:
                    if (P == null || P.HasExited) { HandleExit(); break; }
                    Evaluate();
                    break;
            }
        }

        bool TryAdopt()
        {
            try
            {
                if (!File.Exists(PidFile)) return false;
                string[] a = File.ReadAllText(PidFile).Trim().Split('|'); int pid; long ticks;
                if (a.Length != 2 || !int.TryParse(a[0], out pid) || !long.TryParse(a[1], out ticks)) { File.Delete(PidFile); return false; }
                Process p = ProcTools.Get(pid);
                if (p == null || !ProcTools.SamePath(ProcTools.PathOf(pid), ExpectedExe) ||
                    Math.Abs(p.StartTime.ToUniversalTime().Ticks - ticks) > 50000000L) { File.Delete(PidFile); return false; }
                P = p; Pid = pid; StartedUtc = p.StartTime.ToUniversalTime(); startedAt = DateTime.UtcNow;
                Log.Info("Application", Name + ": adopted the already-running PC Watch process (pid " + pid + ")");
                SetState("Starting", "");
                return true;
            }
            catch { return false; }
        }

        void Start()
        {
            string err; ProcessStartInfo psi;
            if (!Prepare(out err, out psi)) { SetState("Error", err); return; }
            try
            {
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
                lastErr = "";
                Process p = Process.Start(psi);
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) HandleLine(e.Data, false); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) HandleLine(e.Data, true); };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                P = p; Pid = p.Id; StartedUtc = DateTime.UtcNow; startedAt = StartedUtc;
                Directory.CreateDirectory(Paths.RunDir);
                File.WriteAllText(PidFile, Pid + "|" + p.StartTime.ToUniversalTime().Ticks);
                SetState("Starting", "");
            }
            catch (Exception ex) { SetState("Error", "Could not start " + Name + ": " + ex.Message); }
        }

        void HandleLine(string line, bool isErr)
        {
            string l = Log.Redact(line);
            if (isErr) lastErr = l.Length > 300 ? l.Substring(0, 300) : l;
            Log.Write(LineLevel(l, isErr), Cat, l);
            try { OnLine(l, isErr); } catch { }
        }

        void HandleExit()
        {
            int code = -1; try { code = P.ExitCode; } catch { }
            Log.Warn("Application", Name + " process exited unexpectedly (exit code " + code + ")");
            Cleanup();
            string fatal = FatalMessage(code);
            if (fatal != null) { SetState("Error", fatal); return; }
            DateTime now = DateTime.UtcNow;
            crashes.Add(now); crashes.RemoveAll(delegate(DateTime t) { return (now - t).TotalMinutes > 5; });
            string why = Name + " exited (code " + code + ")" + (lastErr.Length > 0 ? ": " + lastErr : "");
            if (crashes.Count >= 5) { SetState("Error", Name + " keeps crashing. Last problem: " + why); return; }
            nextRetry = now.AddSeconds(backoff); backoff = Math.Min(30, backoff * 2);
            SetState("Restarting", why);
        }

        protected void ForceRestart(string why)
        {
            Log.Warn("Application", Name + ": restarting - " + why);
            Stop();
            nextRetry = DateTime.UtcNow.AddSeconds(2);
            SetState("Restarting", why);
        }
        protected void Fail(string msg) { Stop(); SetState("Error", msg); }

        void Cleanup()
        {
            try { if (File.Exists(PidFile)) File.Delete(PidFile); } catch { }
            P = null; Pid = 0;
        }

        // Graceful stop first, then a forced kill of this process tree only
        public void Stop()
        {
            if (P == null) return;
            try { GracefulStop(); } catch { }
            try
            {
                for (int i = 0; i < 30 && !P.HasExited; i++) Thread.Sleep(200);
                if (!P.HasExited) { Log.Warn("Application", Name + " did not exit gracefully; terminating process tree " + Pid); ProcTools.KillTree(Pid); P.WaitForExit(3000); }
            }
            catch { }
            Log.Info("Application", Name + " stopped");
            Cleanup();
        }
    }

    // ---- Python server --------------------------------------------------------
    internal sealed class ServerProc : ManagedProcess
    {
        int fails;
        public ServerProc() { Name = "server"; }
        protected override string Cat { get { return "serverproc"; } }
        protected override string ExpectedExe { get { return Settings.Get("python_path"); } }

        protected override bool Prepare(out string error, out ProcessStartInfo psi)
        {
            psi = null; error = "";
            string py = Settings.Get("python_path"), script = Path.Combine(Paths.ServerDir, "server.py");
            if (!File.Exists(py)) { error = "Python was not found at '" + py + "'. Run Repair (About page) or fix the Python path in Settings."; return false; }
            if (!File.Exists(script)) { error = "Server file missing: " + script + ". Run Repair from the About page."; return false; }
            int port = Settings.GetInt("server_port", 8765);
            if (PortTools.InUse(port)) { error = "Port " + port + " is already used by another program. Choose a different port in Settings."; return false; }
            psi = new ProcessStartInfo(py, "-u \"" + script + "\" --config \"" + Paths.SettingsFile + "\"");
            psi.WorkingDirectory = Paths.ServerDir;
            psi.EnvironmentVariables["PYTHONPATH"] = Path.Combine(Paths.ServerDir, "lib");
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            fails = 0;
            return true;
        }
        protected override string FatalMessage(int code)
        {
            if (code == 10) return "The server could not bind to port " + Settings.GetInt("server_port", 8765) + " (already in use). Change the port in Settings.";
            return null;
        }
        protected override void GracefulStop() { LocalApi.Post("/api/local/shutdown", "{}"); }
        protected override void Evaluate()
        {
            bool ok = LocalApi.Health();
            if (State == "Starting")
            {
                if (ok) { fails = 0; SetState("Running", ""); }
                else if ((DateTime.UtcNow - startedAt).TotalSeconds > 25) Fail("The server did not answer within 25 seconds. See the Server log for details.");
            }
            else if (ok) fails = 0;
            else if (++fails >= 8) { fails = 0; ForceRestart("server stopped responding"); }
        }
    }

    // ---- ngrok tunnel ---------------------------------------------------------
    internal sealed class TunnelProc : ManagedProcess
    {
        public const int ApiPort = 47040;
        public volatile string Url = "";
        volatile string pendingFatal;
        DateTime reconnectSince = DateTime.MinValue;
        public TunnelProc() { Name = "tunnel"; }
        protected override string Cat { get { return "ngrok"; } }
        protected override string ExpectedExe { get { return Settings.Get("ngrok_path"); } }

        protected override bool Prepare(out string error, out ProcessStartInfo psi)
        {
            psi = null; error = ""; pendingFatal = null; Url = "";
            string exe = Settings.Get("ngrok_path");
            if (!File.Exists(exe)) { error = "ngrok was not found at '" + exe + "'. Run Repair (About page) or set the ngrok path in Settings."; return false; }
            string token = Secrets.Load(Paths.TokenFile);
            if (string.IsNullOrEmpty(token)) { error = "No ngrok authtoken is configured. Open Tunnel > 'Set authtoken' and paste the token from dashboard.ngrok.com."; return false; }
            Log.AddSecret(token);
            if (PortTools.InUse(ApiPort)) { error = "Local port " + ApiPort + " (ngrok status interface) is in use by another program."; return false; }
            int port = Settings.GetInt("tunnel_port", 0); if (port <= 0) port = Settings.GetInt("server_port", 8765);
            string cfg = Path.Combine(Paths.ConfigDir, "ngrok.yml");
            File.WriteAllText(cfg, "version: \"3\"\r\nagent:\r\n  web_addr: 127.0.0.1:" + ApiPort + "\r\n");
            string dom = Settings.Get("ngrok_domain").Trim();
            string args = "http 127.0.0.1:" + port + " --config \"" + cfg + "\" --log stdout --log-format json --inspect=false";
            if (dom.Length > 0 && Regex.IsMatch(dom, @"^[A-Za-z0-9.-]+$")) args += " --url https://" + dom;
            psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = Paths.ToolsDir;
            psi.EnvironmentVariables["NGROK_AUTHTOKEN"] = token;   // never put the token on the command line
            return true;
        }

        protected override string LineLevel(string line, bool isErr)
        {
            if (line.Contains("\"lvl\":\"eror\"") || line.Contains("\"lvl\":\"crit\"")) return "ERROR";
            if (line.Contains("\"lvl\":\"warn\"")) return "WARNING";
            if (line.Contains("\"lvl\":\"dbug\"")) return "DEBUG";
            return isErr ? "ERROR" : "INFO";
        }

        protected override void OnLine(string line, bool isErr)
        {
            if (line.Contains("ERR_NGROK_105") || line.Contains("ERR_NGROK_106") || line.Contains("ERR_NGROK_107") || line.Contains("authentication failed"))
                pendingFatal = "ngrok rejected the authtoken (invalid or revoked). Use Tunnel > 'Set authtoken' with a fresh token from dashboard.ngrok.com.";
            else if (line.Contains("ERR_NGROK_108"))
                pendingFatal = "Your ngrok account already has another agent session online (free plans allow one). Stop the other ngrok agent or upgrade the plan.";
            else if (line.Contains("ERR_NGROK_334"))
                pendingFatal = "The reserved ngrok domain is already online elsewhere. Stop the other tunnel using that domain.";
        }

        protected override void GracefulStop()
        {
            try
            {
                string t; int c = Http.Send("GET", "http://127.0.0.1:" + ApiPort + "/api/tunnels", null, null, 1500, true, out t);
                var d = c == 200 ? Json.Obj(t) : null;
                if (d != null && d.ContainsKey("tunnels"))
                {
                    var arr = d["tunnels"] as System.Collections.IEnumerable;
                    if (arr != null) foreach (object o in arr)
                    {
                        string n = Json.Str(o as Dictionary<string, object>, "name");
                        if (n.Length > 0) { string x; Http.Send("DELETE", "http://127.0.0.1:" + ApiPort + "/api/tunnels/" + Uri.EscapeDataString(n), null, null, 2000, true, out x); }
                    }
                }
            }
            catch { }
            Thread.Sleep(400);
            if (P != null && !P.HasExited) ProcTools.KillTree(Pid);   // ngrok has no console to signal; the API call above closes tunnels cleanly first
        }

        string QueryUrl()
        {
            string t; int c = Http.Send("GET", "http://127.0.0.1:" + ApiPort + "/api/tunnels", null, null, 1500, true, out t);
            if (c != 200) return null;
            var d = Json.Obj(t);
            if (d == null || !d.ContainsKey("tunnels")) return null;
            var arr = d["tunnels"] as System.Collections.IEnumerable;
            if (arr == null) return null;
            foreach (object o in arr) { string u = Json.Str(o as Dictionary<string, object>, "public_url"); if (u.StartsWith("https://")) return u; }
            return null;
        }

        protected override void Evaluate()
        {
            if (pendingFatal != null) { string m = pendingFatal; pendingFatal = null; Fail(m); return; }
            string url = QueryUrl();
            if (url != null)
            {
                if (url != Url) { Log.Info("Application", "Public tunnel URL " + (Url == "" ? "is" : "changed to") + ": " + url); Url = url; }
                if (State != "Running") SetState("Running", "");
                return;
            }
            bool net = NetworkInterface.GetIsNetworkAvailable();
            if (State == "Starting")
            {
                if ((DateTime.UtcNow - startedAt).TotalSeconds > 30) { reconnectSince = DateTime.UtcNow; SetState("Reconnecting", net ? "Waiting for ngrok to connect..." : "No internet connection."); }
            }
            else if (State == "Running") { reconnectSince = DateTime.UtcNow; Url = ""; SetState("Reconnecting", net ? "Connection to ngrok lost; reconnecting..." : "No internet connection."); }
            else if (State == "Reconnecting")
            {
                SetState("Reconnecting", net ? "Connection to ngrok lost; reconnecting..." : "No internet connection.");
                if ((DateTime.UtcNow - reconnectSince).TotalSeconds > 90 && net) ForceRestart("tunnel did not recover within 90 seconds");
            }
        }
        public void ClearUrl() { Url = ""; }
    }

    // ---- Supervisor loop ------------------------------------------------------
    internal sealed class Supervisor
    {
        public readonly ServerProc Server = new ServerProc();
        public readonly TunnelProc Tunnel = new TunnelProc();
        Thread th; volatile bool stop; long seenS, seenT;

        public void Start()
        {
            var d = Desired.Load(); seenS = Desired.Num(d, "restart_server"); seenT = Desired.Num(d, "restart_tunnel");
            th = new Thread(Loop); th.IsBackground = true; th.Name = "PCWatch supervisor"; th.Start();
        }
        void Loop()
        {
            while (!stop)
            {
                try { Step(); } catch (Exception ex) { Log.Error("Application", "Supervisor error: " + ex.Message); }
                for (int i = 0; i < 10 && !stop; i++) Thread.Sleep(100);
            }
        }
        static bool Consume(string name)
        {
            try { string f = Path.Combine(Paths.RunDir, "requests", name + ".req"); if (File.Exists(f)) { File.Delete(f); return true; } } catch { }
            return false;
        }
        void Step()
        {
            var d = Desired.Load();
            long s = Desired.Num(d, "restart_server"), t = Desired.Num(d, "restart_tunnel");
            bool rs = s != seenS, rt = t != seenT; seenS = s; seenT = t;
            if (Consume("restart_server")) { rs = true; Desired.Set("server", "1"); d["server"] = "1"; }
            if (Consume("restart_tunnel")) { rt = true; Desired.Set("tunnel", "1"); d["tunnel"] = "1"; }
            Server.Tick(Desired.Flag(d, "server"), rs);
            Tunnel.Tick(Desired.Flag(d, "tunnel"), rt);
            Publish();
        }
        void Publish()
        {
            var st = new Dictionary<string, string>();
            st["heartbeat_unix"] = StatusFile.UnixNow().ToString();
            st["host_pid"] = Process.GetCurrentProcess().Id.ToString();
            st["server_state"] = Server.State; st["server_pid"] = Server.Pid.ToString();
            st["server_started_unix"] = Server.State == "Running" || Server.State == "Starting" ? ((long)(Server.StartedUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString() : "0";
            st["server_error"] = Server.Error;
            st["tunnel_state"] = Tunnel.State; st["tunnel_pid"] = Tunnel.Pid.ToString();
            st["tunnel_error"] = Tunnel.Error; st["tunnel_url"] = Tunnel.State == "Running" ? Tunnel.Url : "";
            try { Directory.CreateDirectory(Paths.RunDir); Ini.Write(StatusFile.FilePath, st); } catch { }
        }
        public void Shutdown()
        {
            stop = true;
            try { if (th != null) th.Join(8000); } catch { }
            Tunnel.Stop(); Server.Stop();
            try { File.Delete(StatusFile.FilePath); } catch { }
            Log.Info("Application", "Background host shut down");
        }
    }

    // ---- Tray icon / session banner ------------------------------------------
    internal static class IconFactory
    {
        public static GraphicsPath RoundPath(RectangleF rc, float r)
        {
            var p = new GraphicsPath(); float d = r * 2;
            p.AddArc(rc.X, rc.Y, d, d, 180, 90); p.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
            p.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90); p.AddArc(rc.X, rc.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
        public static Bitmap Draw(int size, Color bg)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = RoundPath(new RectangleF(1, 1, size - 2, size - 2), size * 0.22f))
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                using (var pen = new Pen(Color.White, size * 0.07f)) g.DrawEllipse(pen, size * 0.2f, size * 0.32f, size * 0.6f, size * 0.36f);
                using (var wb = new SolidBrush(Color.White)) g.FillEllipse(wb, size * 0.41f, size * 0.41f, size * 0.18f, size * 0.18f);
            }
            return bmp;
        }
        public static Icon MakeIcon(Color c)
        {
            using (var b = Draw(32, c)) { IntPtr h = b.GetHicon(); return (Icon)Icon.FromHandle(h).Clone(); }
        }
        // .ico with one 256x256 PNG image (used for PCWatch.exe's file icon)
        public static byte[] IcoBytes()
        {
            using (var bmp = Draw(256, Color.FromArgb(79, 140, 255)))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png); byte[] png = ms.ToArray();
                using (var o = new MemoryStream())
                using (var w = new BinaryWriter(o))
                {
                    w.Write((short)0); w.Write((short)1); w.Write((short)1);
                    w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32); w.Write(png.Length); w.Write(22);
                    w.Write(png); w.Flush();
                    return o.ToArray();
                }
            }
        }
    }

    // Always-on-top red banner shown while a remote screen/camera session is active
    internal sealed class SessionBanner : Form
    {
        readonly Label lbl = new Label();
        public Action OnStop;
        public SessionBanner()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(190, 30, 45); Size = new Size(470, 46);
            lbl.ForeColor = Color.White; lbl.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold); lbl.AutoSize = false;
            lbl.Location = new Point(12, 0); lbl.Size = new Size(350, 46); lbl.TextAlign = ContentAlignment.MiddleLeft;
            var b = new Button { Text = "STOP", FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(190, 30, 45), Location = new Point(372, 8), Size = new Size(86, 30), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            b.FlatAppearance.BorderSize = 0;
            b.Click += delegate { if (OnStop != null) OnStop(); };
            Controls.Add(lbl); Controls.Add(b);
            var wa = Screen.PrimaryScreen.WorkingArea; Location = new Point(wa.Right - Width - 12, wa.Top + 12);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; } }
        public void SetText(string t) { lbl.Text = "\u25CF " + t; }
    }

    internal sealed class HostContext : ApplicationContext
    {
        readonly Supervisor sup = new Supervisor();
        readonly NotifyIcon ni = new NotifyIcon();
        readonly SessionBanner banner = new SessionBanner();
        readonly WinTimer timer = new WinTimer();
        readonly ToolStripMenuItem miServer = new ToolStripMenuItem(), miTunnel = new ToolStripMenuItem();
        readonly Icon iconOk = IconFactory.MakeIcon(Color.FromArgb(79, 140, 255)), iconRec = IconFactory.MakeIcon(Color.FromArgb(200, 30, 45));
        EventWaitHandle balloonEv; bool polling, wasActive, exiting;

        public HostContext()
        {
            var h = banner.Handle;   // force handle creation so BeginInvoke works from worker threads
            banner.OnStop = delegate { ThreadPool.QueueUserWorkItem(delegate { LocalApi.Post("/api/local/session/stop", "{\"type\":\"all\"}"); }); };
            var menu = new ContextMenuStrip();
            var head = new ToolStripMenuItem("PC Watch is installed and running") { Enabled = false };
            var open = new ToolStripMenuItem("Open dashboard"); open.Click += delegate { OpenDashboard(); };
            miServer.Click += delegate { var d = Desired.Load(); if (sup.Server.State == "Stopped" || sup.Server.State == "Error") Desired.StartServer(); else Desired.StopServer(); };
            miTunnel.Click += delegate { if (sup.Tunnel.State == "Stopped" || sup.Tunnel.State == "Error") Desired.StartTunnel(); else Desired.StopTunnel(); };
            var exit = new ToolStripMenuItem("Exit PC Watch (stops server and tunnel)"); exit.Click += delegate { ExitAll(); };
            menu.Items.AddRange(new ToolStripItem[] { head, new ToolStripSeparator(), open, new ToolStripSeparator(), miServer, miTunnel, new ToolStripSeparator(), exit });
            ni.ContextMenuStrip = menu; ni.Icon = iconOk; ni.Text = "PC Watch is running"; ni.Visible = true;
            ni.DoubleClick += delegate { OpenDashboard(); };

            sup.Start();
            timer.Interval = 1500; timer.Tick += delegate { RefreshUi(); }; timer.Start();
            SystemEvents.SessionEnding += delegate { if (!exiting) { exiting = true; sup.Shutdown(); } };

            // The dashboard signals this event when it is hidden to the tray, so we can show a "still running" balloon
            balloonEv = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\PCWatch.Host.Balloon");
            var exitEv = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\PCWatch.Host.Exit");   // set by the dashboard / uninstaller
            var t = new Thread(delegate()
            {
                while (!exiting)
                {
                    int idx = WaitHandle.WaitAny(new WaitHandle[] { balloonEv, exitEv }, 1000);
                    if (exiting) break;
                    if (idx == 0)
                        try { banner.BeginInvoke(new Action(delegate { ni.ShowBalloonTip(4000, "PC Watch", "PC Watch is installed and running in the background. Server and tunnel keep running.", ToolTipIcon.Info); })); } catch { }
                    else if (idx == 1)
                        try { banner.BeginInvoke(new Action(delegate { ExitNow(); })); } catch { }
                }
            });
            t.IsBackground = true; t.Start();
        }

        void OpenDashboard()
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = false, WorkingDirectory = Paths.InstallDir }); }
            catch (Exception ex) { Log.Error("Application", "Could not open dashboard: " + ex.Message); }
        }

        void RefreshUi()
        {
            string s = sup.Server.State, t = sup.Tunnel.State;
            string tip = "PC Watch - Server: " + s + ", Tunnel: " + t;
            ni.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            miServer.Text = (s == "Stopped" || s == "Error") ? "Start server" : "Stop server";
            miTunnel.Text = (t == "Stopped" || t == "Error") ? "Start tunnel" : "Stop tunnel";
            if (polling) return;
            polling = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool scr = false, cam = false;
                try
                {
                    if (s == "Running")
                    {
                        var d = LocalApi.Get("/api/local/sessions");
                        if (d != null) { scr = d.ContainsKey("screen") && d["screen"] != null; cam = d.ContainsKey("camera") && d["camera"] != null; }
                    }
                }
                catch { }
                try { banner.BeginInvoke(new Action(delegate { ApplySessions(scr, cam); polling = false; })); } catch { polling = false; }
            });
        }

        void ApplySessions(bool scr, bool cam)
        {
            bool any = scr || cam;
            if (any)
            {
                banner.SetText((scr ? "SCREEN SHARING ACTIVE" : "") + (scr && cam ? "  |  " : "") + (cam ? "CAMERA ACTIVE" : ""));
                if (!banner.Visible) banner.Show();
                if (!wasActive) ni.ShowBalloonTip(6000, "PC Watch", "A remote " + (scr ? "screen sharing" : "camera") + " session has started.", ToolTipIcon.Warning);
                ni.Icon = iconRec;
            }
            else { if (banner.Visible) banner.Hide(); ni.Icon = iconOk; }
            wasActive = any;
        }

        void ExitAll()
        {
            if (MessageBox.Show("Exit the PC Watch background component?\r\n\r\nThe server and the tunnel will be stopped and the remote endpoint will become unavailable.",
                "PC Watch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            ExitNow();
        }

        void ExitNow()
        {
            if (exiting) return;
            exiting = true; timer.Stop(); ni.Text = "PC Watch - stopping...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                sup.Shutdown();
                try { banner.BeginInvoke(new Action(delegate { ni.Visible = false; ni.Dispose(); ExitThread(); })); } catch { Environment.Exit(0); }
            });
        }
    }

    internal static class HostApp
    {
        static Mutex mutex;
        public static int Run(string[] args)
        {
            bool created;
            mutex = new Mutex(true, "Local\\PCWatch.Host", out created);
            if (!created) return 0;     // already running: never start a duplicate host
            if (!File.Exists(Paths.SettingsFile)) { MessageBox.Show("PC Watch is not configured. Run setup.exe first.", "PC Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning); return 1; }
            Directory.CreateDirectory(Paths.RunDir);
            if (Array.IndexOf(args, "--startup") >= 0) Desired.ApplyStartupMode();
            Log.Info("Application", "Background host started (pid " + Process.GetCurrentProcess().Id + ")");
            Application.Run(new HostContext());
            return 0;
        }
    }

    internal static class HostLauncher
    {
        public static bool IsRunning()
        {
            try { Mutex m; if (Mutex.TryOpenExisting("Local\\PCWatch.Host", out m)) { m.Dispose(); return true; } }
            catch (UnauthorizedAccessException) { return true; }
            catch { }
            return false;
        }
        // Starts the hidden background component (never the dashboard window)
        public static void EnsureRunning()
        {
            if (IsRunning()) return;
            var psi = new ProcessStartInfo(Application.ExecutablePath, "--background") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Paths.InstallDir };
            Process.Start(psi);
        }
    }

    // ========================================================================
    //  EMBEDDED PYTHON SERVER + MOBILE WEB UI (written to Server\ by setup)
    //  Both texts use single quotes only, so they fit in C# verbatim strings.
    // ========================================================================
    internal static class ServerFiles
    {
        public const string ServerPy = @"# PC Watch server: authenticated management API + mobile web UI.
# Standard library + psutil (required). mss / Pillow / opencv are optional (screen / camera).
import sys, os, io, re, json, time, base64, hashlib, hmac, secrets, socket, platform, threading, traceback, ctypes
from collections import deque
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from http.cookies import SimpleCookie
from urllib.parse import urlparse, parse_qs

VERSION = '1.0.0'
START = time.time()
CFG_PATH = ''
PORT = 8765
HTTPD = None
LOCK = threading.RLock()
CAMLOCK = threading.Lock()
_cache = {'t': 0, 'd': {}}


def arg(name):
    a = sys.argv
    if name in a:
        i = a.index(name)
        if i + 1 < len(a):
            return a[i + 1]
    return None


def read_ini(path):
    d = {}
    try:
        with open(path, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if not line or line[0] in '#;' or '=' not in line:
                    continue
                k, v = line.split('=', 1)
                d[k.strip()] = v.strip()
    except Exception:
        pass
    return d


def cfg(key, default=''):
    with LOCK:
        if time.time() - _cache['t'] > 2:
            _cache['d'] = read_ini(CFG_PATH)
            _cache['t'] = time.time()
        return _cache['d'].get(key, default)


def cfg_int(key, default):
    try:
        return int(cfg(key, str(default)))
    except Exception:
        return default


def cfg_dir():
    return os.path.dirname(os.path.abspath(CFG_PATH))


def base_dir():
    return os.path.dirname(cfg_dir())


def run_dir():
    d = os.path.join(cfg_dir(), 'run')
    os.makedirs(d, exist_ok=True)
    return d


def log_dir():
    d = cfg('log_dir') or os.path.join(base_dir(), 'Logs')
    os.makedirs(d, exist_ok=True)
    return d


def update_ini(changes):
    d = read_ini(CFG_PATH)
    d.update(changes)
    tmp = CFG_PATH + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        for k, v in d.items():
            f.write('%s=%s\n' % (k, v))
    os.replace(tmp, CFG_PATH)
    _cache['t'] = 0


# ---------------------------------------------------------------- logging
LEVELS = {'DEBUG': 10, 'INFO': 20, 'WARNING': 30, 'ERROR': 40}
LOGFILES = {'Server': 'server.log', 'Authentication': 'auth.log', 'Connections': 'connections.log'}
SECRET_RX = re.compile(r'(?i)\b(token|password|passwd|secret|cookie|authorization)\b(\s*[:=]\s*)[^\s,;]+')


def rotate(path):
    for i in range(4, 0, -1):
        a, b = '%s.%d' % (path, i), '%s.%d' % (path, i + 1)
        if os.path.exists(a):
            os.replace(a, b)
    os.replace(path, path + '.1')


def log(cat, level, msg):
    try:
        if LEVELS.get(level, 20) < LEVELS.get(cfg('log_level', 'INFO').upper(), 20):
            return
        msg = SECRET_RX.sub(r'\1\2***', str(msg)).replace('\r', ' ').replace('\n', ' ')
        t = time.time()
        stamp = time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(t)) + '.%03d' % int((t * 1000) % 1000)
        line = '%s|%s|%s|%s\n' % (stamp, level, cat, msg)
        path = os.path.join(log_dir(), LOGFILES[cat])
        with LOCK:
            try:
                if os.path.exists(path) and os.path.getsize(path) > 2 * 1024 * 1024:
                    rotate(path)
            except Exception:
                pass
            with open(path, 'a', encoding='utf-8') as f:
                f.write(line)
    except Exception:
        pass


# ---------------------------------------------------------------- authentication
def pw_problem(pw, user):
    if len(pw) < 12:
        return 'Password must be at least 12 characters.'
    if not (re.search('[a-z]', pw) and re.search('[A-Z]', pw) and re.search('[0-9]', pw) and re.search('[^A-Za-z0-9]', pw)):
        return 'Password needs lowercase, uppercase, a digit and a symbol.'
    if user and user.lower() in pw.lower():
        return 'Password must not contain the username.'
    return None


def hash_pw(pw, salt, iters):
    return hashlib.pbkdf2_hmac('sha256', pw.encode('utf-8'), salt, iters)


def auth_file():
    return os.path.join(cfg_dir(), 'auth.json')


def load_auth():
    try:
        with open(auth_file(), 'r', encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return None


def init_auth():
    # username and password arrive on stdin (never on the command line)
    user = sys.stdin.readline().rstrip('\r\n')
    pw = sys.stdin.readline().rstrip('\r\n')
    if not re.fullmatch(r'[A-Za-z0-9_.-]{3,32}', user):
        print('ERROR: invalid username')
        return 2
    p = pw_problem(pw, user)
    if p:
        print('ERROR: ' + p)
        return 2
    salt = os.urandom(16)
    iters = 600000
    data = {'user': user, 'salt': base64.b64encode(salt).decode(), 'iters': iters,
            'hash': base64.b64encode(hash_pw(pw, salt, iters)).decode()}
    tmp = auth_file() + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(data, f)
    os.replace(tmp, auth_file())
    log('Authentication', 'INFO', 'Remote login credentials were set for user ' + user)
    print('OK')
    return 0


SESSIONS = {}
FAILS = {}
ACCT = []
RATE = {}
IDLE = 1800
MAXAGE = 43200


def new_session(ip, user):
    sid = secrets.token_urlsafe(32)
    now = time.time()
    with LOCK:
        SESSIONS[sid] = {'csrf': secrets.token_urlsafe(24), 'created': now, 'last': now, 'ip': ip, 'user': user}
    return sid


def get_session(sid):
    now = time.time()
    with LOCK:
        s = SESSIONS.get(sid)
        if not s:
            return None
        if now - s['last'] > IDLE or now - s['created'] > MAXAGE:
            del SESSIONS[sid]
            return None
        s['last'] = now
        return s


def write_last(ip, user):
    try:
        p = os.path.join(run_dir(), 'last_connection.ini')
        with open(p + '.tmp', 'w', encoding='utf-8') as f:
            f.write('time=%s\nip=%s\nuser=%s\n' % (time.strftime('%Y-%m-%d %H:%M:%S'), ip, user))
        os.replace(p + '.tmp', p)
    except Exception:
        pass


# ---------------------------------------------------------------- explicit screen / camera sessions
MEDIA = {'screen': None, 'camera': None}
CAM = {'cap': None}


def media_info():
    with LOCK:
        out = {}
        for k in ('screen', 'camera'):
            m = MEDIA[k]
            out[k] = None if not m else {'since': int(m['since']), 'until': int(m['until']), 'by': m['by'], 'ip': m['ip']}
        return out


def media_start(kind, ip, user):
    if cfg('allow_' + kind) != '1':
        return 'This feature is disabled. Enable it on the PC in PC Watch > Remote Session.'
    with LOCK:
        if MEDIA[kind]:
            return 'A session is already active.'
        if kind == 'screen':
            try:
                import mss
            except Exception:
                return 'Screen capture packages are not installed. Run Repair in PC Watch.'
        else:
            try:
                import cv2
                cap = cv2.VideoCapture(0, cv2.CAP_DSHOW)
                if not cap.isOpened():
                    return 'No camera could be opened.'
                CAM['cap'] = cap
            except Exception:
                return 'Camera support is not installed or no camera was found.'
        mins = max(1, min(240, cfg_int('session_minutes', 30)))
        MEDIA[kind] = {'since': time.time(), 'until': time.time() + mins * 60, 'by': user, 'ip': ip}
    log('Connections', 'WARNING', '%s session STARTED by %s from %s (max %d min)' % (kind, user, ip, mins))
    return None


def media_stop(kind, why):
    with LOCK:
        if not MEDIA[kind]:
            return False
        MEDIA[kind] = None
        cap = CAM.get('cap') if kind == 'camera' else None
        if kind == 'camera':
            CAM['cap'] = None
    try:
        if cap is not None:
            cap.release()
    except Exception:
        pass
    log('Connections', 'WARNING', '%s session ENDED (%s)' % (kind, why))
    return True


def media_active(kind):
    with LOCK:
        m = MEDIA[kind]
        return bool(m) and time.time() < m['until']


def grab_screen():
    import mss
    with mss.mss() as sct:
        shot = sct.grab(sct.monitors[1])
    try:
        from PIL import Image
        img = Image.frombytes('RGB', shot.size, shot.bgra, 'raw', 'BGRX')
        if img.width > 1280:
            img = img.resize((1280, int(img.height * 1280 / img.width)))
        b = io.BytesIO()
        img.save(b, 'JPEG', quality=55)
        return b.getvalue(), 'image/jpeg'
    except ImportError:
        import mss.tools
        return mss.tools.to_png(shot.rgb, shot.size), 'image/png'


def grab_camera():
    import cv2
    with CAMLOCK:
        cap = CAM.get('cap')
        if cap is None:
            raise RuntimeError('camera closed')
        ok, fr = cap.read()
    if not ok:
        raise RuntimeError('camera read failed')
    ok, buf = cv2.imencode('.jpg', fr, [int(cv2.IMWRITE_JPEG_QUALITY), 60])
    return buf.tobytes(), 'image/jpeg'


def watchdog():
    while True:
        time.sleep(1)
        try:
            for k in ('screen', 'camera'):
                with LOCK:
                    m = MEDIA[k]
                if m and time.time() >= m['until']:
                    media_stop(k, 'time limit reached')
            now = time.time()
            with LOCK:
                for sid in [s for s, v in SESSIONS.items() if now - v['last'] > IDLE or now - v['created'] > MAXAGE]:
                    del SESSIONS[sid]
                for ip in [i for i, q in RATE.items() if not q or now - q[-1] > 120]:
                    del RATE[ip]
        except Exception:
            pass


# ---------------------------------------------------------------- system information and operations
def caps():
    c = {'psutil': False, 'mss': False, 'pillow': False, 'opencv': False}
    for k, m in (('psutil', 'psutil'), ('mss', 'mss'), ('pillow', 'PIL'), ('opencv', 'cv2')):
        try:
            __import__(m)
            c[k] = True
        except Exception:
            pass
    return c


def local_ips():
    out = []
    try:
        import psutil
        for name, addrs in psutil.net_if_addrs().items():
            for a in addrs:
                if a.family == socket.AF_INET and not a.address.startswith('127.') and not a.address.startswith('169.254.'):
                    out.append(a.address)
    except Exception:
        pass
    return out


def sysinfo():
    import psutil
    vm = psutil.virtual_memory()
    return {'pc': socket.gethostname(), 'os': platform.platform(), 'version': platform.version(), 'arch': platform.machine(),
            'processor': platform.processor(), 'cores': psutil.cpu_count(), 'cpu': psutil.cpu_percent(interval=None),
            'ram_total': vm.total, 'ram_used': vm.used, 'ram_avail': vm.available, 'ram_percent': vm.percent,
            'uptime': int(time.time() - psutil.boot_time()), 'ips': local_ips(), 'port': PORT,
            'server_uptime': int(time.time() - START)}


def tunnel_status():
    d = read_ini(os.path.join(run_dir(), 'status.ini'))
    fresh = abs(time.time() - float(d.get('heartbeat_unix', '0') or 0)) < 30
    return {'host': fresh, 'tunnel': d.get('tunnel_state', 'Unknown') if fresh else 'Unknown',
            'url': d.get('tunnel_url', '') if fresh else '', 'server': d.get('server_state', 'Unknown') if fresh else 'Unknown'}


def request_host(name):
    d = os.path.join(run_dir(), 'requests')
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, name + '.req'), 'w') as f:
        f.write(str(time.time()))


SETTINGS_PAGES = {'network': 'ms-settings:network', 'windowsupdate': 'ms-settings:windowsupdate', 'display': 'ms-settings:display',
                  'sound': 'ms-settings:sound', 'bluetooth': 'ms-settings:bluetooth', 'privacy': 'ms-settings:privacy',
                  'apps': 'ms-settings:appsfeatures', 'about': 'ms-settings:about'}

OPS = [{'name': 'pc_info', 'title': 'Retrieve PC information', 'sensitive': False},
       {'name': 'check_network', 'title': 'Check network connectivity', 'sensitive': False},
       {'name': 'check_health', 'title': 'Check server health', 'sensitive': False},
       {'name': 'restart_server', 'title': 'Restart PC Watch server', 'sensitive': True},
       {'name': 'restart_tunnel', 'title': 'Restart ngrok tunnel', 'sensitive': True},
       {'name': 'open_settings', 'title': 'Open a Windows settings page on the PC', 'sensitive': True},
       {'name': 'set_config', 'title': 'Change log level / log retention', 'sensitive': True}]


def run_op(name, body):
    if name == 'pc_info':
        return sysinfo()
    if name == 'check_network':
        res = {}
        try:
            t = time.time()
            socket.create_connection(('1.1.1.1', 443), 3).close()
            res['internet'] = 'reachable (%d ms)' % int((time.time() - t) * 1000)
        except Exception as e:
            res['internet'] = 'unreachable: ' + str(e)
        try:
            socket.gethostbyname('ngrok.com')
            res['dns'] = 'ok'
        except Exception as e:
            res['dns'] = 'failed: ' + str(e)
        res['tunnel'] = tunnel_status()
        return res
    if name == 'check_health':
        import psutil
        p = psutil.Process()
        return {'ok': True, 'pid': p.pid, 'uptime': int(time.time() - START), 'threads': p.num_threads(),
                'memory_mb': round(p.memory_info().rss / 1048576, 1), 'sessions': len(SESSIONS), 'version': VERSION}
    if name == 'restart_server':
        request_host('restart_server')
        return {'requested': True, 'note': 'The server will restart in a few seconds; reload this page afterwards.'}
    if name == 'restart_tunnel':
        request_host('restart_tunnel')
        return {'requested': True, 'note': 'The tunnel will restart; the public URL may change on free ngrok plans.'}
    if name == 'open_settings':
        uri = SETTINGS_PAGES.get(str(body.get('page', '')))
        if not uri:
            raise ValueError('Unknown settings page')
        os.startfile(uri)
        return {'opened': uri}
    if name == 'set_config':
        ch = {}
        if 'log_level' in body:
            lv = str(body['log_level']).upper()
            if lv not in LEVELS:
                raise ValueError('Invalid log level')
            ch['log_level'] = lv
        if 'log_retention_days' in body:
            n = int(body['log_retention_days'])
            if not 1 <= n <= 365:
                raise ValueError('Retention must be 1-365 days')
            ch['log_retention_days'] = str(n)
        if not ch:
            raise ValueError('Nothing to change')
        update_ini(ch)
        return {'updated': ch}
    raise KeyError(name)


def tail(path, n):
    try:
        with open(path, 'rb') as f:
            f.seek(0, 2)
            size = f.tell()
            f.seek(max(0, size - 65536))
            data = f.read().decode('utf-8', 'replace')
        return data.splitlines()[-n:]
    except Exception:
        return []


LOGCATS = {'application': 'application.log', 'server': 'server.log', 'ngrok': 'ngrok.log',
           'authentication': 'auth.log', 'connections': 'connections.log'}

# ---------------------------------------------------------------- HTTP
class H(BaseHTTPRequestHandler):
    server_version = 'PCWatch'
    sys_version = ''
    protocol_version = 'HTTP/1.1'
    timeout = 20

    def log_message(self, *a):
        pass

    def send_bytes(self, code, data, ctype, extra=None):
        self.send_response(code)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('X-Frame-Options', 'DENY')
        self.send_header('Referrer-Policy', 'no-referrer')
        for k, v in (extra or []):
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(data)

    def send_json(self, code, obj, extra=None):
        self.send_bytes(code, json.dumps(obj).encode('utf-8'), 'application/json; charset=utf-8', extra)

    def peer_local(self):
        return self.client_address[0] in ('127.0.0.1', '::1')

    def is_local(self):
        # direct loopback call from PCWatch.exe: ngrok always adds X-Forwarded-For, so tunnel traffic never qualifies
        if not self.peer_local():
            return False
        for h in ('X-Forwarded-For', 'X-Forwarded-Host', 'Forwarded', 'X-Real-Ip'):
            if self.headers.get(h):
                return False
        return True

    def client_ip(self):
        if self.peer_local():
            xf = self.headers.get('X-Forwarded-For')
            if xf:
                return xf.split(',')[0].strip()[:64]
        return self.client_address[0]

    def rate_ok(self):
        now = time.time()
        with LOCK:
            q = RATE.setdefault(self.ip, deque())
            while q and now - q[0] > 60:
                q.popleft()
            if len(q) >= 600:
                return False
            q.append(now)
        return True

    def origin_ok(self):
        o = self.headers.get('Origin')
        return (not o) or urlparse(o).netloc == self.headers.get('Host', '')

    def session(self):
        try:
            c = SimpleCookie(self.headers.get('Cookie', ''))
            return get_session(c['pcw_sid'].value) if 'pcw_sid' in c else None
        except Exception:
            return None

    def do_GET(self):
        self.route('GET')

    def do_POST(self):
        self.route('POST')

    def route(self, method):
        try:
            self.ip = self.client_ip()
            if not self.rate_ok():
                return self.send_json(429, {'error': 'Too many requests'})
            u = urlparse(self.path)
            path = u.path
            q = parse_qs(u.query)
            if method == 'GET' and path == '/':
                return self.page()
            if path == '/api/health':
                return self.send_json(200, {'ok': True, 'app': 'pc-watch'})
            body = {}
            if method == 'POST':
                n = int(self.headers.get('Content-Length') or 0)
                if n > 8192:
                    return self.send_json(413, {'error': 'Request too large'})
                raw = self.rfile.read(n) if n else b''
                try:
                    body = json.loads(raw.decode('utf-8')) if raw else {}
                except Exception:
                    return self.send_json(400, {'error': 'Invalid JSON'})
                if not isinstance(body, dict):
                    return self.send_json(400, {'error': 'Invalid JSON'})
            if path.startswith('/api/local/'):
                return self.local(method, path, body)
            if method == 'POST' and not self.origin_ok():
                return self.send_json(403, {'error': 'Bad origin'})
            if path == '/api/login' and method == 'POST':
                return self.login(body)
            s = self.session()
            if not s:
                return self.send_json(401, {'error': 'Not authenticated'})
            if method == 'POST' and not hmac.compare_digest(self.headers.get('X-CSRF-Token', ''), s['csrf']):
                return self.send_json(403, {'error': 'Bad CSRF token'})
            return self.api(method, path, q, body, s)
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception:
            log('Server', 'ERROR', 'Unhandled error: ' + traceback.format_exc().splitlines()[-1])
            try:
                self.send_json(500, {'error': 'Internal server error'})
            except Exception:
                pass

    def page(self):
        nonce = secrets.token_urlsafe(16)
        html = WEB.replace('__NONCE__', nonce).encode('utf-8')
        csp = ('default-src ' + chr(39) + 'none' + chr(39) + '; script-src ' + chr(39) + 'nonce-' + nonce + chr(39) +
               '; style-src ' + chr(39) + 'unsafe-inline' + chr(39) + '; img-src blob: data:; connect-src ' + chr(39) + 'self' + chr(39) +
               '; base-uri ' + chr(39) + 'none' + chr(39) + '; form-action ' + chr(39) + 'none' + chr(39))
        self.send_bytes(200, html, 'text/html; charset=utf-8', [('Content-Security-Policy', csp)])

    def login(self, body):
        now = time.time()
        ip = self.ip
        with LOCK:
            f = [t for t in FAILS.get(ip, []) if now - t < 900]
            FAILS[ip] = f
            ACCT[:] = [t for t in ACCT if now - t < 900]
            locked = len(f) >= 5 or len(ACCT) >= 20
        if locked:
            log('Authentication', 'WARNING', 'Login blocked (too many failures) from ' + ip)
            return self.send_json(429, {'error': 'Too many failed attempts. Try again in 15 minutes.'}, [('Retry-After', '900')])
        user = str(body.get('username', ''))[:64]
        pw = str(body.get('password', ''))[:256]
        a = load_auth()
        ok = False
        if a:
            h = hash_pw(pw, base64.b64decode(a['salt']), int(a['iters']))
            ok = hmac.compare_digest(h, base64.b64decode(a['hash'])) and hmac.compare_digest(user.encode(), a['user'].encode())
        else:
            hash_pw(pw, os.urandom(16), 600000)
        if not ok:
            with LOCK:
                FAILS.setdefault(ip, []).append(now)
                ACCT.append(now)
            log('Authentication', 'WARNING', 'Failed login from ' + ip)
            time.sleep(0.6)
            return self.send_json(401, {'error': 'Invalid username or password'})
        with LOCK:
            FAILS.pop(ip, None)
        sid = new_session(ip, a['user'])
        log('Authentication', 'INFO', 'Login OK for %s from %s' % (a['user'], ip))
        log('Connections', 'INFO', 'Remote session opened by %s from %s' % (a['user'], ip))
        write_last(ip, a['user'])
        ck = 'pcw_sid=%s; Path=/; HttpOnly; SameSite=Strict; Max-Age=%d' % (sid, MAXAGE)
        if self.headers.get('X-Forwarded-Proto', '') == 'https':
            ck += '; Secure'
        s = SESSIONS[sid]
        self.send_json(200, {'user': a['user'], 'csrf': s['csrf']}, [('Set-Cookie', ck)])

    def api(self, method, path, q, body, s):
        if path == '/api/session' and method == 'GET':
            return self.send_json(200, {'user': s['user'], 'csrf': s['csrf']})
        if path == '/api/logout' and method == 'POST':
            with LOCK:
                for k in [k for k, v in SESSIONS.items() if v is s]:
                    del SESSIONS[k]
            log('Authentication', 'INFO', 'Logout: ' + s['user'])
            return self.send_json(200, {'ok': True}, [('Set-Cookie', 'pcw_sid=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0')])
        if path == '/api/status' and method == 'GET':
            d = sysinfo()
            d['tunnel'] = tunnel_status()
            d['media'] = media_info()
            d['allow'] = {'screen': cfg('allow_screen') == '1', 'camera': cfg('allow_camera') == '1'}
            d['caps'] = caps()
            return self.send_json(200, d)
        if path == '/api/logs' and method == 'GET':
            f = LOGCATS.get((q.get('cat') or ['server'])[0])
            if not f:
                return self.send_json(400, {'error': 'Unknown log'})
            return self.send_json(200, {'lines': tail(os.path.join(log_dir(), f), 200)})
        if path == '/api/ops' and method == 'GET':
            return self.send_json(200, {'ops': OPS, 'pages': sorted(SETTINGS_PAGES.keys())})
        if path.startswith('/api/ops/') and method == 'POST':
            name = path[9:]
            op = [o for o in OPS if o['name'] == name]
            if not op:
                return self.send_json(404, {'error': 'Unknown operation'})
            if op[0]['sensitive'] and body.get('confirm') is not True:
                return self.send_json(400, {'error': 'This operation needs explicit confirmation'})
            try:
                res = run_op(name, body)
                log('Server', 'INFO', 'Operation %s executed by %s from %s' % (name, s['user'], self.ip))
                return self.send_json(200, {'result': res})
            except (ValueError, KeyError) as e:
                return self.send_json(400, {'error': str(e)})
            except Exception as e:
                log('Server', 'ERROR', 'Operation %s failed: %s' % (name, e))
                return self.send_json(500, {'error': 'Operation failed: ' + str(e)})
        if path == '/api/session/start' and method == 'POST':
            kind = str(body.get('type', ''))
            if kind not in MEDIA:
                return self.send_json(400, {'error': 'Unknown session type'})
            err = media_start(kind, self.ip, s['user'])
            return self.send_json(409 if err else 200, {'error': err} if err else {'media': media_info()})
        if path == '/api/session/stop' and method == 'POST':
            kind = str(body.get('type', ''))
            if kind not in MEDIA:
                return self.send_json(400, {'error': 'Unknown session type'})
            media_stop(kind, 'stopped by ' + s['user'])
            return self.send_json(200, {'media': media_info()})
        if path in ('/api/frame/screen', '/api/frame/camera') and method == 'GET':
            kind = path.rsplit('/', 1)[1]
            if not media_active(kind):
                return self.send_json(403, {'error': 'No active ' + kind + ' session'})
            try:
                data, ctype = grab_screen() if kind == 'screen' else grab_camera()
            except Exception as e:
                log('Server', 'ERROR', kind + ' capture failed: ' + str(e))
                media_stop(kind, 'capture error')
                return self.send_json(500, {'error': 'Capture failed'})
            return self.send_bytes(200, data, ctype)
        return self.send_json(404, {'error': 'Not found'})

    def local(self, method, path, body):
        if not self.is_local():
            return self.send_json(404, {'error': 'Not found'})
        key = self.headers.get('X-PCWatch-Local-Key', '')
        want = cfg('local_key_sha256')
        if not want or not hmac.compare_digest(hashlib.sha256(key.encode('utf-8')).hexdigest(), want):
            log('Authentication', 'WARNING', 'Rejected local API call with a bad key')
            return self.send_json(403, {'error': 'Forbidden'})
        if path == '/api/local/status' and method == 'GET':
            last = read_ini(os.path.join(run_dir(), 'last_connection.ini'))
            return self.send_json(200, {'version': VERSION, 'pid': os.getpid(), 'uptime': int(time.time() - START), 'port': PORT,
                                        'logins': len(SESSIONS), 'last': last, 'caps': caps(), 'media': media_info()})
        if path == '/api/local/sessions' and method == 'GET':
            return self.send_json(200, media_info())
        if path == '/api/local/session/stop' and method == 'POST':
            t = str(body.get('type', 'all'))
            for k in ('screen', 'camera'):
                if t in ('all', k):
                    media_stop(k, 'stopped on the PC')
            return self.send_json(200, media_info())
        if path == '/api/local/revoke' and method == 'POST':
            with LOCK:
                SESSIONS.clear()
            log('Authentication', 'WARNING', 'All remote logins were revoked from the PC')
            return self.send_json(200, {'ok': True})
        if path == '/api/local/shutdown' and method == 'POST':
            self.send_json(200, {'ok': True})
            threading.Thread(target=shutdown).start()
            return
        return self.send_json(404, {'error': 'Not found'})


def shutdown():
    time.sleep(0.2)
    for k in ('screen', 'camera'):
        media_stop(k, 'server shutting down')
    if HTTPD is not None:
        HTTPD.shutdown()


def main():
    global CFG_PATH, PORT, HTTPD
    CFG_PATH = arg('--config') or ''
    if not CFG_PATH:
        print('ERROR: --config is required')
        return 2
    if '--init-auth' in sys.argv:
        return init_auth()
    if '--check' in sys.argv:
        print(json.dumps(caps()))
        return 0
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass
    PORT = cfg_int('server_port', 8765)
    try:
        HTTPD = ThreadingHTTPServer(('127.0.0.1', PORT), H)
    except OSError as e:
        log('Server', 'ERROR', 'Cannot listen on port %d: %s' % (PORT, e))
        print('ERROR: port %d unavailable' % PORT)
        return 10
    try:
        import psutil
        psutil.cpu_percent(interval=None)
    except Exception:
        log('Server', 'ERROR', 'psutil is missing: system information is unavailable. Run Repair.')
    threading.Thread(target=watchdog, daemon=True).start()
    log('Server', 'INFO', 'PC Watch server %s listening on 127.0.0.1:%d (pid %d)' % (VERSION, PORT, os.getpid()))
    try:
        HTTPD.serve_forever()
    finally:
        for k in ('screen', 'camera'):
            media_stop(k, 'server stopped')
        log('Server', 'INFO', 'Server stopped')
    return 0


WEB = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'web.html'), encoding='utf-8').read()

if __name__ == '__main__':
    sys.exit(main())
";

        public const string WebHtml = @"<!doctype html>
<html lang='en'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<title>PC Watch</title>
<style>
:root{--bg:#12141a;--card:#1b1e27;--line:#2a2e3a;--tx:#e8ebf2;--mu:#9198a8;--ac:#4f8cff;--ok:#2ecc71;--bad:#eb5757;--wn:#f2b535}
*{box-sizing:border-box}
body{margin:0;font:15px system-ui,Segoe UI,Roboto,sans-serif;background:var(--bg);color:var(--tx)}
.wrap{max-width:880px;margin:0 auto;padding:14px}
header{display:flex;align-items:center;justify-content:space-between;padding:12px 14px}
h1{font-size:19px;margin:0}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:14px;margin:0 0 12px}
.card h2{font-size:12px;margin:0 0 10px;color:var(--mu);text-transform:uppercase;letter-spacing:.06em}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px}
.kv{display:flex;justify-content:space-between;gap:10px;padding:6px 0;border-bottom:1px solid var(--line)}
.kv:last-child{border:0}.kv span:first-child{color:var(--mu)}.kv span:last-child{text-align:right;word-break:break-all}
nav{display:flex;gap:6px;overflow-x:auto;padding:0 14px 8px}
nav button{white-space:nowrap}
button,select,input{font:inherit;color:var(--tx);background:#262a36;border:1px solid var(--line);border-radius:10px;padding:9px 13px}
button{cursor:pointer}button.on{background:var(--ac);border-color:var(--ac)}button.bad{background:#7d2b2b;border-color:#a33}
input{width:100%;margin:6px 0}
.badge{display:inline-block;padding:3px 10px;border-radius:99px;font-size:13px;font-weight:600}
.ok{background:#1d4d33;color:#5fe39a}.bad2{background:#552626;color:#ff8d8d}.wn{background:#5a4817;color:#ffd166}.gr{background:#333845;color:#aab}
.bar{height:10px;background:#262a36;border-radius:6px;overflow:hidden}.bar div{height:100%;background:var(--ac)}
pre{background:#0e1015;border:1px solid var(--line);border-radius:10px;padding:10px;overflow:auto;max-height:380px;font:12px Consolas,monospace;white-space:pre-wrap;word-break:break-all}
img{max-width:100%;border-radius:10px;border:1px solid var(--line)}
.row{display:flex;gap:8px;flex-wrap:wrap;margin:8px 0}
.err{color:var(--bad);min-height:20px}
[hidden]{display:none!important}
</style></head><body>
<div class='wrap'>
<div id='login' class='card' style='max-width:380px;margin:12vh auto 0'>
 <h1>PC Watch</h1><p style='color:var(--mu)'>Sign in to manage this PC.</p>
 <input id='u' placeholder='Username' autocomplete='username'>
 <input id='p' type='password' placeholder='Password' autocomplete='current-password'>
 <div class='err' id='lerr'></div>
 <button class='on' id='lgo' style='width:100%'>Sign in</button>
</div>
<div id='app' hidden>
 <header><h1>PC Watch <span id='pcn' style='color:var(--mu);font-weight:400'></span></h1><button id='out'>Sign out</button></header>
 <nav id='tabs'></nav>
 <div id='view'></div>
</div></div>
<script nonce='__NONCE__'>
var csrf = '', tab = 'status', poll = null, frameT = null, info = {};
var TABS = [['status','Status'],['system','System'],['logs','Logs'],['tasks','Tasks'],['screen','Screen'],['camera','Camera']];
function $(id){ return document.getElementById(id); }
function el(t, c, x){ var e = document.createElement(t); if (c) e.className = c; if (x !== undefined) e.textContent = x; return e; }
function api(path, method, body){
  var o = {method: method || 'GET', credentials: 'same-origin', headers: {}};
  if (o.method === 'POST'){ o.headers['Content-Type'] = 'application/json'; o.headers['X-CSRF-Token'] = csrf; o.body = JSON.stringify(body || {}); }
  return fetch(path, o).then(function(r){
    if (r.status === 401 && path !== '/api/login'){ showLogin(); throw new Error('Signed out'); }
    return r.json().then(function(j){ if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status)); return j; });
  });
}
function showLogin(){ stopAll(); $('app').hidden = true; $('login').hidden = false; }
function showApp(){ $('login').hidden = true; $('app').hidden = false; buildTabs(); go('status'); }
function stopAll(){ if (poll) clearInterval(poll); poll = null; if (frameT) clearTimeout(frameT); frameT = null; }
function fmtB(n){ return (n / 1073741824).toFixed(1) + ' GB'; }
function fmtT(s){ var d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60); return (d ? d + 'd ' : '') + h + 'h ' + m + 'm'; }
function badge(txt){
  var c = {Running:'ok', Starting:'wn', Reconnecting:'wn', Restarting:'wn', Stopped:'bad2', Error:'bad2'}[txt] || 'gr';
  return el('span', 'badge ' + c, txt);
}
function kv(card, k, v){ var r = el('div', 'kv'); r.appendChild(el('span', '', k)); var s = el('span'); if (v instanceof Node) s.appendChild(v); else s.textContent = v; r.appendChild(s); card.appendChild(r); }
function card(title){ var c = el('div', 'card'); c.appendChild(el('h2', '', title)); return c; }
function bar(p){ var b = el('div', 'bar'), i = el('div'); i.style.width = Math.min(100, p) + '%'; b.appendChild(i); return b; }
function buildTabs(){
  var n = $('tabs'); n.textContent = '';
  TABS.forEach(function(t){ var b = el('button', '', t[1]); b.id = 't_' + t[0]; b.onclick = function(){ go(t[0]); }; n.appendChild(b); });
}
function go(t){
  stopAll(); tab = t;
  TABS.forEach(function(x){ $('t_' + x[0]).className = x[0] === t ? 'on' : ''; });
  render();
  if (t === 'status' || t === 'system') poll = setInterval(render, 3000);
}
function render(){
  var v = $('view');
  if (tab === 'status' || tab === 'system') api('/api/status').then(function(d){ info = d; $('pcn').textContent = d.pc; if (tab === 'status') drawStatus(v, d); else drawSystem(v, d); }).catch(err(v));
  else if (tab === 'logs') drawLogs(v);
  else if (tab === 'tasks') drawTasks(v);
  else drawMedia(v, tab);
}
function err(v){ return function(e){ if (e.message !== 'Signed out') v.textContent = 'Error: ' + e.message; }; }
function drawStatus(v, d){
  var f = document.createDocumentFragment();
  var c = card('Services');
  kv(c, 'PC Watch server', badge('Running'));
  kv(c, 'Tunnel', badge(d.tunnel.tunnel));
  kv(c, 'Public URL', d.tunnel.url || '-');
  f.appendChild(c);
  c = card('Resources');
  kv(c, 'CPU ' + Math.round(d.cpu) + '%', bar(d.cpu));
  kv(c, 'RAM ' + fmtB(d.ram_used) + ' / ' + fmtB(d.ram_total), bar(d.ram_percent));
  kv(c, 'PC uptime', fmtT(d.uptime));
  kv(c, 'Server uptime', fmtT(d.server_uptime));
  f.appendChild(c);
  c = card('Sessions');
  kv(c, 'Screen sharing', d.media.screen ? 'ACTIVE' : (d.allow.screen ? 'allowed, not active' : 'disabled on PC'));
  kv(c, 'Camera', d.media.camera ? 'ACTIVE' : (d.allow.camera ? 'allowed, not active' : 'disabled on PC'));
  f.appendChild(c);
  v.textContent = ''; v.appendChild(f);
}
function drawSystem(v, d){
  var c = card('PC information');
  kv(c, 'PC name', d.pc); kv(c, 'Operating system', d.os); kv(c, 'Version', d.version); kv(c, 'Architecture', d.arch);
  kv(c, 'Processor', d.processor + ' (' + d.cores + ' logical cores)'); kv(c, 'CPU usage', Math.round(d.cpu) + '%');
  kv(c, 'Total RAM', fmtB(d.ram_total)); kv(c, 'Used RAM', fmtB(d.ram_used)); kv(c, 'Available RAM', fmtB(d.ram_avail));
  kv(c, 'System uptime', fmtT(d.uptime)); kv(c, 'Local IPs', d.ips.join(', ') || '-'); kv(c, 'Server port', String(d.port));
  v.textContent = ''; v.appendChild(c);
}
function drawLogs(v){
  v.textContent = '';
  var c = card('Logs'), s = el('select'), pre = el('pre');
  ['server','application','ngrok','authentication','connections'].forEach(function(x){ var o = el('option', '', x); o.value = x; s.appendChild(o); });
  function load(){ api('/api/logs?cat=' + s.value).then(function(d){ pre.textContent = d.lines.join('\n') || '(empty)'; pre.scrollTop = pre.scrollHeight; }).catch(function(e){ pre.textContent = e.message; }); }
  s.onchange = load; var b = el('button', '', 'Refresh'); b.onclick = load;
  var r = el('div', 'row'); r.appendChild(s); r.appendChild(b); c.appendChild(r); c.appendChild(pre); v.appendChild(c); load();
}
function drawTasks(v){
  v.textContent = '';
  api('/api/ops').then(function(d){
    var c = card('Permitted maintenance tasks'), out = el('pre'), pg = el('select');
    d.pages.forEach(function(p){ var o = el('option', '', p); o.value = p; pg.appendChild(o); });
    d.ops.forEach(function(op){
      var r = el('div', 'row'), b = el('button', op.sensitive ? 'bad' : '', op.title);
      b.onclick = function(){
        var body = {};
        if (op.sensitive){ if (!confirm(op.title + '?\nThis affects the PC.')) return; body.confirm = true; }
        if (op.name === 'open_settings') body.page = pg.value;
        if (op.name === 'set_config'){ var lv = prompt('Log level (DEBUG, INFO, WARNING, ERROR):', 'INFO'); if (!lv) return; body.log_level = lv; }
        out.textContent = 'Working...';
        api('/api/ops/' + op.name, 'POST', body).then(function(j){ out.textContent = JSON.stringify(j.result, null, 2); }).catch(function(e){ out.textContent = 'Error: ' + e.message; });
      };
      r.appendChild(b); if (op.name === 'open_settings') r.appendChild(pg); c.appendChild(r);
    });
    c.appendChild(out); v.appendChild(c);
  }).catch(err(v));
}
function drawMedia(v, kind){
  v.textContent = '';
  var c = card(kind === 'screen' ? 'Remote screen sharing' : 'Remote camera'), st = el('div', 'kv'), img = el('img'), msg = el('div', 'err');
  var r = el('div', 'row'), go1 = el('button', 'on', 'Start session'), stop = el('button', 'bad', 'Stop session');
  c.appendChild(el('p', '', 'A session must be started explicitly. The PC shows a red banner while it is active and it ends automatically after the time limit.'));
  c.appendChild(st); r.appendChild(go1); r.appendChild(stop); c.appendChild(r); c.appendChild(msg); c.appendChild(img); v.appendChild(c);
  function loop(){
    fetch('/api/frame/' + kind, {credentials: 'same-origin'}).then(function(r){
      if (!r.ok) throw new Error('ended');
      return r.blob();
    }).then(function(b){ var old = img.src; img.src = URL.createObjectURL(b); if (old) URL.revokeObjectURL(old); frameT = setTimeout(loop, kind === 'screen' ? 500 : 250); })
      .catch(function(){ refresh(); });
  }
  function refresh(){
    api('/api/status').then(function(d){
      var m = d.media[kind], allowed = d.allow[kind];
      st.textContent = ''; st.appendChild(el('span', '', 'Status')); st.appendChild(el('span', '', m ? 'ACTIVE' : (allowed ? 'inactive' : 'disabled on the PC')));
      go1.disabled = !!m || !allowed; stop.disabled = !m;
      if (m) { if (!frameT) loop(); } else { if (frameT) clearTimeout(frameT); frameT = null; img.removeAttribute('src'); }
    }).catch(function(e){ msg.textContent = e.message; });
  }
  go1.onclick = function(){ msg.textContent = ''; api('/api/session/start', 'POST', {type: kind}).then(refresh).catch(function(e){ msg.textContent = e.message; }); };
  stop.onclick = function(){ api('/api/session/stop', 'POST', {type: kind}).then(refresh); };
  refresh();
}
$('lgo').onclick = function(){
  $('lerr').textContent = '';
  api('/api/login', 'POST', {username: $('u').value, password: $('p').value}).then(function(j){ csrf = j.csrf; $('p').value = ''; showApp(); }).catch(function(e){ $('lerr').textContent = e.message; });
};
$('p').onkeydown = function(e){ if (e.key === 'Enter') $('lgo').click(); };
$('out').onclick = function(){ api('/api/logout', 'POST').then(showLogin).catch(showLogin); };
api('/api/session').then(function(j){ csrf = j.csrf; showApp(); }).catch(function(){ showLogin(); });
</script></body></html>
";
    }

    // ========================================================================
    //  UI TOOLKIT (dark, rounded cards, status badges, vector icons)
    // ========================================================================
    internal static class Theme
    {
        public static Color Bg, Side, Card, Border, Text, Muted, Accent, Good, Bad, Warn, Gray, Input;
        static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
        static Theme() { Apply("Dark"); }
        public static void Apply(string name)
        {
            if (name == "Light") { Bg = C(240, 242, 247); Side = C(226, 230, 238); Card = Color.White; Border = C(209, 214, 225); Text = C(24, 28, 38); Muted = C(98, 106, 124); Input = C(238, 241, 246); }
            else if (name == "Midnight") { Bg = C(8, 10, 18); Side = C(4, 5, 11); Card = C(16, 19, 32); Border = C(32, 37, 58); Text = C(232, 235, 245); Muted = C(135, 142, 165); Input = C(24, 28, 46); }
            else { Bg = C(18, 20, 26); Side = C(13, 15, 20); Card = C(27, 30, 39); Border = C(42, 46, 58); Text = C(232, 235, 242); Muted = C(145, 152, 168); Input = C(37, 41, 53); }
            Accent = C(79, 140, 255); Good = C(46, 204, 113); Bad = C(235, 87, 87); Warn = C(242, 181, 53); Gray = C(120, 126, 140);
        }
        // Green = running/connected, Red = stopped/error, Yellow = starting/reconnecting, Gray = unknown
        public static Color StateColor(string s)
        {
            switch (s)
            {
                case "Running": return Good;
                case "Stopped": case "Error": return Bad;
                case "Starting": case "Restarting": case "Reconnecting": case "Stopping": return Warn;
                default: return Gray;
            }
        }
    }

    internal static class UI
    {
        public static Font F(float size, bool bold) { return new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular); }
        public static Color Lerp(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        public static Label Lbl(string t, float size, bool bold, Color c)
        {
            return new Label { Text = t, Font = F(size, bold), ForeColor = c, BackColor = Color.Transparent, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        }
        public static void Style(Control c)
        {
            c.BackColor = Theme.Input; c.ForeColor = Theme.Text; c.Anchor = AnchorStyles.Left | AnchorStyles.Right; c.Font = F(9.5f, false);
            var tb = c as TextBox; if (tb != null) tb.BorderStyle = BorderStyle.FixedSingle;
            var cb = c as ComboBox; if (cb != null) { cb.FlatStyle = FlatStyle.Flat; cb.DropDownStyle = ComboBoxStyle.DropDownList; }
        }
        public static CheckBox Chk(string text) { return new CheckBox { Text = text, ForeColor = Theme.Text, BackColor = Color.Transparent, Dock = DockStyle.Fill, Font = F(9.5f, false) }; }
    }

    internal sealed class Btn : Button
    {
        // kind: 0 normal, 1 primary, 2 danger
        public Btn(string text, int kind)
        {
            Text = text; FlatStyle = FlatStyle.Flat; Cursor = Cursors.Hand; Font = UI.F(9.5f, kind == 1);
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; MinimumSize = new Size(0, 34); Padding = new Padding(10, 0, 10, 0);
            Color bg = kind == 1 ? Theme.Accent : kind == 2 ? Color.FromArgb(168, 52, 52) : Theme.Input;
            BackColor = bg; ForeColor = kind == 0 ? Theme.Text : Color.White; Margin = new Padding(0, 0, 8, 0);
            FlatAppearance.BorderSize = 0; FlatAppearance.MouseOverBackColor = UI.Lerp(bg, Color.White, 0.18f);
        }
    }

    internal sealed class Card : Panel
    {
        readonly TableLayoutPanel grid = new TableLayoutPanel();
        int rows, used;
        public Card(string title)
        {
            DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Bg; Padding = new Padding(18, 12, 18, 12); Margin = new Padding(0, 0, 0, 14);
            grid.Dock = DockStyle.Fill; grid.ColumnCount = 2; grid.BackColor = Color.Transparent;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
            Controls.Add(grid);
            AddWide(UI.Lbl(title.ToUpperInvariant(), 8.5f, true, Theme.Muted), 28);
        }
        public void AddWide(Control c, int h)
        {
            grid.RowCount = rows + 1; grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            c.Dock = DockStyle.Fill; grid.Controls.Add(c, 0, rows); grid.SetColumnSpan(c, 2);
            rows++; used += h; Height = Padding.Vertical + used;
        }
        public void AddRow(string key, Control val, int h)
        {
            grid.RowCount = rows + 1; grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            grid.Controls.Add(UI.Lbl(key, 9.5f, false, Theme.Muted), 0, rows);
            if (val.Dock == DockStyle.None && val.Anchor == (AnchorStyles.Top | AnchorStyles.Left)) val.Dock = DockStyle.Fill;
            grid.Controls.Add(val, 1, rows);
            rows++; used += h; Height = Padding.Vertical + used;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = IconFactory.RoundPath(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12))
            using (var b = new SolidBrush(Theme.Card))
            using (var pen = new Pen(Theme.Border)) { e.Graphics.FillPath(b, p); e.Graphics.DrawPath(pen, p); }
        }
    }

    // Vertical stack of cards that follows the window width
    internal sealed class Page : FlowLayoutPanel
    {
        public Page()
        {
            FlowDirection = FlowDirection.TopDown; WrapContents = false; AutoScroll = true; Dock = DockStyle.Fill; Visible = false;
            Padding = new Padding(24, 6, 24, 16); BackColor = Theme.Bg;
        }
        public void Add(params Control[] cs) { foreach (Control c in cs) Controls.Add(c); Fit(); }
        void Fit()
        {
            int w = ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
            if (w < 200) return;
            foreach (Control c in Controls) if (c.Width != w) c.Width = w;
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }
    }

    internal sealed class Badge : Control
    {
        string text = "Unknown"; Color col = Theme.Gray;
        public Badge()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent; Size = new Size(150, 28); Font = UI.F(9f, true); Anchor = AnchorStyles.Left;
        }
        public void SetState(string state, string label) { text = label; col = Theme.StateColor(state); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var p = IconFactory.RoundPath(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f))
            using (var b = new SolidBrush(Color.FromArgb(48, col))) g.FillPath(b, p);
            using (var b = new SolidBrush(col)) g.FillEllipse(b, 11, Height / 2f - 4, 8, 8);
            using (var b = new SolidBrush(Theme.Text)) g.DrawString(text, Font, b, new RectangleF(26, 0, Width - 28, Height), new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
        }
    }

    internal sealed class Meter : Control
    {
        double val; string cap = "";
        public Meter()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent; Font = UI.F(9.5f, false);
        }
        public void Set(double v, string caption) { val = v; cap = caption; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            int capW = 150, barW = Math.Max(20, Width - capW - 8), y = Height / 2 - 5;
            using (var p = IconFactory.RoundPath(new RectangleF(0, y, barW, 10), 5)) using (var b = new SolidBrush(Theme.Input)) g.FillPath(b, p);
            float fw = (float)(barW * Math.Max(0, Math.Min(100, val)) / 100.0);
            if (fw > 8)
            {
                Color c = val > 90 ? Theme.Bad : val > 75 ? Theme.Warn : Theme.Accent;
                using (var p = IconFactory.RoundPath(new RectangleF(0, y, fw, 10), 5)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
            }
            using (var b = new SolidBrush(Theme.Text)) g.DrawString(cap, Font, b, new RectangleF(barW + 8, 0, capW, Height), new StringFormat { LineAlignment = StringAlignment.Center });
        }
    }

    internal static class Icons
    {
        // Small vector icons so no icon font is required. Kinds: 0 home 1 server 2 globe 3 monitor 4 remote 5 list 6 gear 7 info
        public static void Draw(Graphics g, int kind, RectangleF r, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using (var p = new Pen(c, Math.Max(1.6f, w / 11f)))
            using (var b = new SolidBrush(c))
            {
                p.LineJoin = LineJoin.Round; p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                switch (kind)
                {
                    case 0:
                        g.DrawPolygon(p, new PointF[] { new PointF(x + w * .5f, y + h * .08f), new PointF(x + w * .95f, y + h * .5f), new PointF(x + w * .82f, y + h * .5f), new PointF(x + w * .82f, y + h * .92f), new PointF(x + w * .18f, y + h * .92f), new PointF(x + w * .18f, y + h * .5f), new PointF(x + w * .05f, y + h * .5f) });
                        break;
                    case 1:
                        g.DrawRectangle(p, x + w * .1f, y + h * .1f, w * .8f, h * .34f); g.DrawRectangle(p, x + w * .1f, y + h * .56f, w * .8f, h * .34f);
                        g.FillEllipse(b, x + w * .2f, y + h * .22f, w * .1f, w * .1f); g.FillEllipse(b, x + w * .2f, y + h * .68f, w * .1f, w * .1f);
                        break;
                    case 2:
                        g.DrawEllipse(p, x + w * .08f, y + h * .08f, w * .84f, h * .84f); g.DrawEllipse(p, x + w * .32f, y + h * .08f, w * .36f, h * .84f);
                        g.DrawLine(p, x + w * .08f, y + h * .5f, x + w * .92f, y + h * .5f);
                        break;
                    case 3:
                        g.DrawRectangle(p, x + w * .06f, y + h * .12f, w * .88f, h * .56f);
                        g.DrawLine(p, x + w * .5f, y + h * .68f, x + w * .5f, y + h * .86f); g.DrawLine(p, x + w * .28f, y + h * .88f, x + w * .72f, y + h * .88f);
                        break;
                    case 4:
                        g.DrawRectangle(p, x + w * .06f, y + h * .12f, w * .88f, h * .62f);
                        g.FillPolygon(b, new PointF[] { new PointF(x + w * .42f, y + h * .28f), new PointF(x + w * .66f, y + h * .43f), new PointF(x + w * .42f, y + h * .58f) });
                        g.DrawLine(p, x + w * .3f, y + h * .88f, x + w * .7f, y + h * .88f);
                        break;
                    case 5:
                        for (int i = 0; i < 3; i++) { g.DrawLine(p, x + w * .3f, y + h * (.22f + i * .28f), x + w * .92f, y + h * (.22f + i * .28f)); g.FillEllipse(b, x + w * .04f, y + h * (.22f + i * .28f) - w * .05f, w * .1f, w * .1f); }
                        break;
                    case 6:
                        g.DrawEllipse(p, x + w * .3f, y + h * .3f, w * .4f, h * .4f);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4; float cx = x + w / 2f, cy = y + h / 2f;
                            g.DrawLine(p, cx + (float)Math.Cos(a) * w * .34f, cy + (float)Math.Sin(a) * h * .34f, cx + (float)Math.Cos(a) * w * .46f, cy + (float)Math.Sin(a) * h * .46f);
                        }
                        break;
                    default:
                        g.DrawEllipse(p, x + w * .08f, y + h * .08f, w * .84f, h * .84f);
                        g.FillEllipse(b, x + w * .45f, y + h * .24f, w * .1f, w * .1f); g.DrawLine(p, x + w * .5f, y + h * .44f, x + w * .5f, y + h * .72f);
                        break;
                }
            }
        }
    }

    internal sealed class NavItem : Control
    {
        readonly int kind; readonly string label; bool sel, hover; float anim; readonly WinTimer t = new WinTimer();
        public NavItem(int kind, string label)
        {
            this.kind = kind; this.label = label; Cursor = Cursors.Hand; Font = UI.F(10f, false);
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            t.Interval = 15;
            t.Tick += delegate { float target = sel ? 1f : 0f; anim += (target - anim) * 0.35f; if (Math.Abs(target - anim) < 0.02f) { anim = target; t.Stop(); } Invalidate(); };
        }
        public bool Selected { get { return sel; } set { sel = value; t.Start(); } }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void Dispose(bool d) { if (d) t.Dispose(); base.Dispose(d); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Theme.Side);
            float k = Math.Max(anim, hover ? 0.45f : 0f);
            using (var p = IconFactory.RoundPath(new RectangleF(0, 0, Width - 1, Height - 1), 9)) using (var b = new SolidBrush(UI.Lerp(Theme.Side, Theme.Card, k))) g.FillPath(b, p);
            using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, Height / 2f - 10 * anim, 3, 20 * anim);
            Icons.Draw(g, kind, new RectangleF(16, (Height - 20) / 2f, 20, 20), UI.Lerp(Theme.Muted, Theme.Accent, anim));
            using (var b = new SolidBrush(UI.Lerp(Theme.Muted, Theme.Text, Math.Max(anim, hover ? 0.7f : 0f))))
                g.DrawString(label, Font, b, new RectangleF(48, 0, Width - 50, Height), new StringFormat { LineAlignment = StringAlignment.Center });
        }
    }

    internal static class Prompt
    {
        public static string Ask(IWin32Window owner, string title, string label, string def, bool password)
        {
            using (var f = new Form())
            {
                f.Text = title; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(440, 150); f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                f.BackColor = Theme.Card; f.ForeColor = Theme.Text; f.Font = UI.F(9.5f, false);
                var l = new Label { Text = label, Left = 16, Top = 14, Width = 408, Height = 40 };
                var tb = new TextBox { Left = 16, Top = 58, Width = 408, Text = def, UseSystemPasswordChar = password, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
                var ok = new Btn("OK", 1) { Left = 240, Top = 100, DialogResult = DialogResult.OK };
                var cn = new Btn("Cancel", 0) { Left = 330, Top = 100, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { l, tb, ok, cn }); f.AcceptButton = ok; f.CancelButton = cn;
                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
            }
        }
    }

    // ------------------------------------------------------------------------
    //  Log reading for the dashboard log viewer
    // ------------------------------------------------------------------------
    internal sealed class LogEntry
    {
        public DateTime Time; public string Level = "", Cat = "", Msg = "", Raw = "";
    }

    internal static class LogReader
    {
        static string[] FilesFor(string cat)
        {
            switch (cat)
            {
                case "Application": return new[] { "application.log" };
                case "Server": return new[] { "server.log", "server-process.log" };
                case "ngrok": return new[] { "ngrok.log" };
                case "Installation": return new[] { "install.log" };
                case "Authentication": return new[] { "auth.log" };
                case "Connections": return new[] { "connections.log" };
                default: return Log.AllFiles;
            }
        }
        public static List<LogEntry> Load(string cat, string search, int max, DateTime after)
        {
            var list = new List<LogEntry>();
            foreach (string f in FilesFor(cat))
            {
                string path = Path.Combine(Log.Dir, f);
                try
                {
                    if (!File.Exists(path)) continue;
                    string text;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long start = Math.Max(0, fs.Length - 300000); fs.Seek(start, SeekOrigin.Begin);
                        using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
                        if (start > 0) { int nl = text.IndexOf('\n'); if (nl >= 0) text = text.Substring(nl + 1); }
                    }
                    foreach (string raw in text.Split('\n'))
                    {
                        string line = raw.TrimEnd('\r'); if (line.Length < 25) continue;
                        string[] p = line.Split(new[] { '|' }, 4); DateTime t;
                        if (p.Length < 4 || !DateTime.TryParse(p[0], out t)) continue;
                        if (t <= after) continue;
                        if (cat == "Errors" && p[1] != "ERROR" && p[1] != "CRITICAL") continue;
                        if (!string.IsNullOrEmpty(search) && line.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        list.Add(new LogEntry { Time = t, Level = p[1], Cat = p[2], Msg = p[3], Raw = line });
                    }
                }
                catch { }
            }
            list.Sort(delegate(LogEntry a, LogEntry b) { return a.Time.CompareTo(b.Time); });
            if (list.Count > max) list.RemoveRange(0, list.Count - max);
            return list;
        }
    }

    internal static class StartupReg
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "PC Watch";
        // Windows startup entry: starts ONLY the hidden background host, never the dashboard window
        public static void Apply(int mode)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
            {
                if (k == null) return;
                if (mode >= 1) k.SetValue(ValueName, "\"" + Paths.ExePath + "\" --background --startup");
                else k.DeleteValue(ValueName, false);
            }
        }
    }

    internal static class AuthTool
    {
        // Sets the remote-login credentials; the Python server hashes the password (PBKDF2) - it travels over stdin only
        public static string Set(string user, string pw)
        {
            string py = Settings.Get("python_path"), script = Path.Combine(Paths.ServerDir, "server.py");
            if (!File.Exists(py) || !File.Exists(script)) return "Python or the server files are missing. Run Repair.";
            string output;
            int code = ProcTools.Run(py, "\"" + script + "\" --init-auth --config \"" + Paths.SettingsFile + "\"", 60000, out output, null, user + "\n" + pw + "\n", Paths.ServerDir);
            if (code == 0 && output.Contains("OK")) { Settings.Set("remote_user", user); return null; }
            return "Could not set credentials: " + output.Trim();
        }
    }

    // ========================================================================
    //  DASHBOARD
    // ========================================================================
    internal sealed class Snapshot
    {
        public bool HostAlive;
        public string ServerState = "Unknown", TunnelState = "Unknown", ServerError = "", TunnelError = "", TunnelUrl = "";
        public int ServerPid, TunnelPid, Logins;
        public long ServerStartedUnix;
        public double Cpu; public ulong RamTotal, RamAvail; public TimeSpan SysUptime;
        public string LastConn = "No remote login yet", Ip = "-", ServerInfo = "";
        public bool ScreenOn, CamOn; public string ScreenInfo = "", CamInfo = "";
        public string[] Recent = new string[0];
    }

    internal static class DashboardApp
    {
        static Mutex mutex;
        public static int Run(string[] args)
        {
            bool created;
            mutex = new Mutex(true, "Local\\PCWatch.Dashboard", out created);
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\PCWatch.Dashboard.Show");
            if (!created) { ev.Set(); return 0; }   // already open: bring the existing window forward instead
            if (!File.Exists(Paths.SettingsFile)) { MessageBox.Show("PC Watch is not installed on this PC. Run setup.exe first.", "PC Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning); return 1; }
            Theme.Apply(Settings.Get("theme"));
            try { HostLauncher.EnsureRunning(); } catch (Exception ex) { Log.Error("Application", "Could not start the background host: " + ex.Message); }
            Log.Info("Application", "Dashboard opened by the user");
            Application.Run(new DashboardForm(ev));
            return 0;
        }
    }

    internal sealed class DashboardForm : Form
    {
        static readonly string[] Names = { "Overview", "Server", "Tunnel", "PC Information", "Remote Session", "Activity Logs", "Settings", "About" };
        readonly List<NavItem> navs = new List<NavItem>();
        readonly Page[] pages = new Page[8];
        readonly Dictionary<string, Label> V = new Dictionary<string, Label>();
        readonly Dictionary<string, Badge> B = new Dictionary<string, Badge>();
        readonly Dictionary<string, Btn> Bt = new Dictionary<string, Btn>();
        readonly Meter mCpu = new Meter(), mRam = new Meter(), mCpu2 = new Meter(), mRam2 = new Meter();
        readonly EventWaitHandle showEv;
        readonly WinTimer logTimer = new WinTimer();
        Label title; int cur = -1; Snapshot snap = new Snapshot(); volatile bool closing; bool stopAllOnExit;
        RichTextBox rtRecent, rtLogs; TextBox tbStatusOut, tbUrl, tbSearch; ComboBox cbCat; Label lblTest;
        DateTime clearedAt = DateTime.MinValue; List<LogEntry> shown = new List<LogEntry>();
        NumericUpDown nPort, nRet, nTunPort, nSess; ComboBox cStartup, cLevel, cTheme; CheckBox kStop, kScreen, kCam;
        TextBox tPy, tNg, tDom, tToken, tLogDir, tUser, tPw1, tPw2; Label tokenState;

        public DashboardForm(EventWaitHandle ev)
        {
            showEv = ev;
            Text = "PC Watch"; Icon = IconFactory.MakeIcon(Theme.Accent);
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(1000, 660); Size = new Size(1140, 740); StartPosition = FormStartPosition.CenterScreen;
            Font = UI.F(9.5f, false); BackColor = Theme.Bg; ForeColor = Theme.Text; DoubleBuffered = true;
            BuildShell();
            BuildOverview(); BuildServer(); BuildTunnel(); BuildPc(); BuildRemote(); BuildLogs(); BuildSettings(); BuildAbout();
            Select(0);
            logTimer.Interval = 2000; logTimer.Tick += delegate { RefreshLogs(); };
            var poll = new Thread(PollLoop); poll.IsBackground = true; poll.Start();
            var lis = new Thread(delegate()
            {
                while (!closing) { if (showEv.WaitOne(1000) && !closing) { try { BeginInvoke(new Action(RestoreFromTray)); } catch { } } }
            });
            lis.IsBackground = true; lis.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int v = Theme.Bg.GetBrightness() < 0.5f ? 1 : 0; Native.DwmSetWindowAttribute(Handle, 20, ref v, 4); } catch { }
        }

        // ---- helpers ---------------------------------------------------------
        Label Val(string id) { var l = UI.Lbl("-", 9.5f, false, Theme.Text); V[id] = l; return l; }
        Badge Bdg(string id) { var b = new Badge(); B[id] = b; return b; }
        Btn Mk(string text, int kind, Action a)
        {
            var b = new Btn(text, kind);
            b.Click += delegate { try { a(); } catch (Exception ex) { Log.Error("Application", ex.ToString()); MessageBox.Show(this, ex.Message, "PC Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
            return b;
        }
        static FlowLayoutPanel Flow(params Control[] cs)
        {
            var f = new FlowLayoutPanel { BackColor = Color.Transparent, WrapContents = true, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
            f.Controls.AddRange(cs); return f;
        }
        void Do(Action a) { try { HostLauncher.EnsureRunning(); } catch { } a(); }
        static string G(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? v : ""; }
        static string Help(string e)
        {
            if (string.IsNullOrEmpty(e)) return "";
            string l = e.ToLowerInvariant(), tip = "";
            if (l.Contains("port")) tip = "Fix: open Settings, choose a free server port (for example 8766), then start again.";
            else if (l.Contains("authtoken")) tip = "Fix: Tunnel page > Set authtoken (token from dashboard.ngrok.com > Your Authtoken).";
            else if (l.Contains("python")) tip = "Fix: About > Repair reinstalls Python and the server files.";
            else if (l.Contains("internet")) tip = "Fix: check the PC's internet connection. The tunnel reconnects by itself.";
            else if (l.Contains("ngrok")) tip = "Fix: see Activity Logs > ngrok, or run About > Repair.";
            return e + (tip.Length > 0 ? "\r\n" + tip : "");
        }

        // ---- shell: sidebar, top bar, navigation -------------------------------
        void BuildShell()
        {
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            for (int i = 0; i < pages.Length; i++) { pages[i] = new Page(); content.Controls.Add(pages[i]); }

            var top = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = Theme.Bg };
            title = new Label { Left = 28, Top = 14, Width = 520, Height = 38, Font = UI.F(17f, true), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft };
            var bar = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, FlowDirection = FlowDirection.LeftToRight };
            bar.Controls.Add(Mk("HIDE TRAY", 1, HideToTray));
            bar.Controls.Add(Mk("Minimize", 0, delegate { WindowState = FormWindowState.Minimized; }));
            bar.Controls.Add(Mk("Exit", 2, ExitDialog));
            top.Controls.Add(title); top.Controls.Add(bar);
            EventHandler pos = delegate { bar.Left = top.Width - bar.Width - 24; bar.Top = 15; };
            top.Resize += pos; bar.SizeChanged += pos;

            var side = new Panel { Dock = DockStyle.Left, Width = 214, BackColor = Theme.Side };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Side, Padding = new Padding(10, 14, 10, 0) };
            flow.Controls.Add(new Label { Text = "PC Watch", Font = UI.F(15f, true), ForeColor = Theme.Text, Width = 192, Height = 46, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(6, 0, 0, 6) });
            for (int i = 0; i < Names.Length; i++)
            {
                int idx = i; var n = new NavItem(i, Names[i]) { Width = 192, Height = 42, Margin = new Padding(0, 2, 0, 2) };
                n.Click += delegate { Select(idx); }; navs.Add(n); flow.Controls.Add(n);
            }
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 78, BackColor = Theme.Side };
            var hb = Bdg("host"); hb.Location = new Point(14, 10); hb.Width = 186; hb.Anchor = AnchorStyles.None;
            bottom.Controls.Add(hb);
            bottom.Controls.Add(new Label { Text = "v" + Program.Version + "   Designed by MD", ForeColor = Theme.Muted, Font = UI.F(8.5f, false), Left = 16, Top = 46, Width = 190, Height = 22 });
            side.Controls.Add(flow); side.Controls.Add(bottom);

            Controls.Add(content); Controls.Add(top); Controls.Add(side);
        }

        void Select(int i)
        {
            if (i == cur) return;
            if (cur >= 0) { pages[cur].Visible = false; navs[cur].Selected = false; }
            cur = i; pages[i].Visible = true; navs[i].Selected = true; title.Text = Names[i];
            if (i == 5) RefreshLogs();
            if (i == 6) LoadSettings();
            logTimer.Enabled = i == 5;
        }
        void GoLogs(string cat) { Select(5); cbCat.SelectedItem = cat; RefreshLogs(); }

        // ---- window management: HIDE TRAY / restore / exit ---------------------
        void HideToTray()
        {
            try { HostLauncher.EnsureRunning(); } catch { }
            try { var e = EventWaitHandle.OpenExisting("Local\\PCWatch.Host.Balloon"); e.Set(); e.Dispose(); } catch { }
            Hide();   // window only: server, tunnel and the background host keep running
            Log.Info("Application", "Dashboard hidden to tray");
        }
        void RestoreFromTray()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true; TopMost = false; Activate(); Native.SetForegroundWindow(Handle);
        }
        void ExitDialog()
        {
            var r = MessageBox.Show(this, "Exit the dashboard.\r\n\r\nYES  = exit and also stop PC Watch (server, tunnel and background component)\r\nNO   = exit the dashboard only; server and tunnel keep running",
                "Exit PC Watch", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            stopAllOnExit = r == DialogResult.Yes; Close();
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (stopAllOnExit) { try { var ev = EventWaitHandle.OpenExisting("Local\\PCWatch.Host.Exit"); ev.Set(); ev.Dispose(); } catch { } }
            else if (Settings.GetBool("stop_on_exit")) Desired.StopBoth();
            closing = true;
            base.OnFormClosing(e);
        }

        // ---- Overview -----------------------------------------------------------
        void BuildOverview()
        {
            var c1 = new Card("Status");
            var strip = new TableLayoutPanel { ColumnCount = 4, RowCount = 2, BackColor = Color.Transparent };
            for (int i = 0; i < 4; i++) strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            strip.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); strip.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            string[] caps = { "SERVER", "TUNNEL", "REMOTE ENDPOINT", "BACKGROUND" }, ids = { "server", "tunnel", "endpoint", "host2" };
            for (int i = 0; i < 4; i++) { strip.Controls.Add(UI.Lbl(caps[i], 8f, true, Theme.Muted), i, 0); strip.Controls.Add(Bdg(ids[i]), i, 1); }
            c1.AddWide(strip, 62);

            var c2 = new Card("Controls");
            c2.AddWide(Flow(Mk("Start both", 1, delegate { Do(Desired.StartBoth); }), Mk("Stop both", 2, delegate { Do(Desired.StopBoth); }),
                Mk("Restart both", 0, delegate { Do(Desired.StartBoth); })), 48);

            var c3 = new Card("Live data");
            c3.AddRow("PC name", Val("pc"), 30); c3.AddRow("Local IP", Val("ip"), 30); c3.AddRow("Server port", Val("port"), 30);
            var up = new Panel { BackColor = Color.Transparent };
            var ul = Val("url"); ul.Dock = DockStyle.Fill;
            var cp = new Btn("Copy", 0) { Dock = DockStyle.Right, AutoSize = false, Width = 74 }; cp.Click += delegate { CopyUrl(); };
            up.Controls.Add(ul); up.Controls.Add(cp);
            c3.AddRow("Public tunnel URL", up, 36);
            c3.AddRow("CPU usage", mCpu, 30); c3.AddRow("RAM usage", mRam, 30);
            c3.AddRow("Server uptime", Val("up"), 30); c3.AddRow("Last connection", Val("last"), 30);

            var c4 = new Card("Recent logs");
            rtRecent = new RichTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = new Font("Consolas", 9f), WordWrap = false };
            c4.AddWide(rtRecent, 150);
            pages[0].Add(c1, c2, c3, c4);
        }

        void CopyUrl()
        {
            if (string.IsNullOrEmpty(snap.TunnelUrl)) { MessageBox.Show(this, "There is no public URL yet. Start the tunnel first.", "PC Watch"); return; }
            Clipboard.SetText(snap.TunnelUrl);
        }

        // ---- Server ------------------------------------------------------------------
        void BuildServer()
        {
            var c1 = new Card("Server status");
            c1.AddRow("Status", Bdg("server_b"), 36); c1.AddRow("Process ID", Val("spid"), 30); c1.AddRow("Uptime", Val("sup"), 30);
            c1.AddRow("Listening on", Val("sport"), 30); c1.AddRow("Remote endpoint", Val("sendp"), 30);
            var er = Val("serr"); er.ForeColor = Theme.Warn; er.AutoEllipsis = false; c1.AddRow("Details", er, 64);

            var c2 = new Card("Controls");
            var start = Mk("Start Server", 1, delegate { Do(Desired.StartServer); }); var stop = Mk("Stop Server", 2, delegate { Do(Desired.StopServer); });
            var rest = Mk("Restart Server", 0, delegate { Do(Desired.StartServer); });
            Bt["srvStart"] = start; Bt["srvStop"] = stop; Bt["srvRestart"] = rest;
            c2.AddWide(Flow(start, stop, rest, Mk("Check Status", 0, CheckServer), Mk("Change Port", 0, ChangePort), Mk("View Logs", 0, delegate { GoLogs("Server"); })), 92);

            var c3 = new Card("Status check (authenticated local request)");
            tbStatusOut = new TextBox { Multiline = true, ReadOnly = true, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9f), Text = "Press 'Check Status' to query the running server." };
            c3.AddWide(tbStatusOut, 120);
            pages[1].Add(c1, c2, c3);
        }

        void CheckServer()
        {
            tbStatusOut.Text = "Checking...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                try
                {
                    int code; var d = LocalApi.Call("GET", "/api/local/status", null, 3000, out code);
                    if (d == null) text = code == 0 ? "The server is not reachable on port " + Settings.GetInt("server_port", 8765) + ". It is stopped or still starting." : "The server answered HTTP " + code + " (local key rejected?).";
                    else text = "Server version : " + Json.Str(d, "version") + "\r\nProcess ID     : " + Json.Str(d, "pid") + "\r\nUptime (s)     : " + Json.Str(d, "uptime") + "\r\nPort           : " + Json.Str(d, "port") + "\r\nRemote logins  : " + Json.Str(d, "logins");
                }
                catch (Exception ex) { text = "Check failed: " + ex.Message; }
                try { BeginInvoke(new Action(delegate { tbStatusOut.Text = text; })); } catch { }
            });
        }

        void ChangePort()
        {
            string s = Prompt.Ask(this, "Change server port", "New server port (1024 - 65535):", Settings.GetInt("server_port", 8765).ToString(), false);
            if (s == null) return;
            int p; if (!int.TryParse(s.Trim(), out p) || p < 1024 || p > 65535) { MessageBox.Show(this, "Enter a number between 1024 and 65535.", "PC Watch"); return; }
            ApplyPort(p);
        }
        void ApplyPort(int p)
        {
            int old = Settings.GetInt("server_port", 8765);
            if (p == old) return;
            if (PortTools.InUse(p)) { MessageBox.Show(this, "Port " + p + " is already in use by another program.", "PC Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Settings.Set("server_port", p.ToString());
            var d = Desired.Load();
            if (Desired.Flag(d, "server")) Do(Desired.StartServer);
            if (Desired.Flag(d, "tunnel")) Do(Desired.StartTunnel);
            Log.Info("Application", "Server port changed from " + old + " to " + p);
        }

        // ---- Tunnel ----------------------------------------------------------------
        void BuildTunnel()
        {
            var c1 = new Card("Tunnel status");
            c1.AddRow("Status", Bdg("tunnel_b"), 36);
            var up = new Panel { BackColor = Color.Transparent };
            tbUrl = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            var cp = new Btn("Copy", 0) { Dock = DockStyle.Right, AutoSize = false, Width = 74 }; cp.Click += delegate { CopyUrl(); };
            up.Controls.Add(tbUrl); up.Controls.Add(cp);
            c1.AddRow("Public HTTPS URL", up, 38);
            c1.AddRow("Process ID", Val("tpid"), 30); c1.AddRow("Forwarding to", Val("tfwd"), 30); c1.AddRow("Authtoken", Val("ttok"), 30);
            var er = Val("terr"); er.ForeColor = Theme.Warn; er.AutoEllipsis = false; c1.AddRow("Details", er, 64);

            var c2 = new Card("Controls");
            var start = Mk("Start Tunnel", 1, delegate { Do(Desired.StartTunnel); }); var stop = Mk("Stop Tunnel", 2, delegate { Do(Desired.StopTunnel); });
            var rest = Mk("Restart Tunnel", 0, delegate { Do(Desired.StartTunnel); });
            Bt["tunStart"] = start; Bt["tunStop"] = stop; Bt["tunRestart"] = rest;
            c2.AddWide(Flow(start, stop, rest, Mk("Test Connectivity", 0, TestTunnel), Mk("Set authtoken", 0, SetToken), Mk("View ngrok Logs", 0, delegate { GoLogs("ngrok"); })), 92);

            var c3 = new Card("Tunnel configuration");
            nTunPort = new NumericUpDown { Minimum = 0, Maximum = 65535, Value = 0 }; UI.Style(nTunPort);
            c3.AddRow("Forward to local port (0 = server port)", nTunPort, 36);
            c3.AddWide(Flow(Mk("Apply tunnel port", 0, delegate
            {
                Settings.Set("tunnel_port", ((int)nTunPort.Value).ToString()); if (Desired.Flag(Desired.Load(), "tunnel")) Do(Desired.StartTunnel);
            })), 46);
            lblTest = Val("ttest"); lblTest.AutoEllipsis = false; c3.AddRow("Connectivity test", lblTest, 44);
            pages[2].Add(c1, c2, c3);
        }

        void SetToken()
        {
            string t = Prompt.Ask(this, "ngrok authtoken", "Paste your ngrok authtoken (stored encrypted for this Windows user, never shown or logged):", "", true);
            if (t == null) return;
            t = t.Trim();
            if (t.Length < 20 || !Regex.IsMatch(t, @"^[A-Za-z0-9_]+$")) { MessageBox.Show(this, "That does not look like an ngrok authtoken.", "PC Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Secrets.Save(Paths.TokenFile, t); Log.AddSecret(t);
            Log.Info("Application", "ngrok authtoken updated");
            if (Desired.Flag(Desired.Load(), "tunnel")) Do(Desired.StartTunnel);
        }

        void TestTunnel()
        {
            string url = snap.TunnelUrl;
            if (string.IsNullOrEmpty(url)) { lblTest.Text = "No public URL: the tunnel is not running."; return; }
            lblTest.Text = "Testing...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                var sw = Stopwatch.StartNew(); string t;
                var h = new Dictionary<string, string>(); h["ngrok-skip-browser-warning"] = "1";
                int c = Http.Send("GET", url + "/api/health", null, h, 12000, false, out t);
                string r = c == 200 && t.Contains("pc-watch") ? "OK: the public URL reaches the PC Watch server (" + sw.ElapsedMilliseconds + " ms)."
                    : c == 502 || c == 503 || c == 404 ? "Tunnel is up but the server did not answer (HTTP " + c + "). Is the server running on the forwarded port?"
                    : c == 0 ? "Not reachable: " + t : "Unexpected answer: HTTP " + c;
                try { BeginInvoke(new Action(delegate { lblTest.Text = r; })); } catch { }
            });
        }

        // ---- PC information ----------------------------------------------------------
        void BuildPc()
        {
            var c = new Card("System");
            string[] k = { "PC name", "Windows edition", "Windows version", "Architecture", "Processor", "Total RAM", "System uptime", "Local IP addresses", "Server port" };
            string[] id = { "i_pc", "i_os", "i_ver", "i_arch", "i_cpu", "i_ramt", "i_up", "i_ips", "i_port" };
            for (int i = 0; i < k.Length; i++) c.AddRow(k[i], Val(id[i]), 30);
            c.AddRow("Processor usage", mCpu2, 30); c.AddRow("Used RAM", mRam2, 30); c.AddRow("Available RAM", Val("i_rama"), 30);
            V["i_pc"].Text = Environment.MachineName; V["i_os"].Text = SysInfo.OsName(); V["i_ver"].Text = SysInfo.OsVersion();
            V["i_arch"].Text = SysInfo.Arch(); V["i_cpu"].Text = SysInfo.Cpu0Name();
            pages[3].Add(c);
        }

        // ---- Remote session ----------------------------------------------------------------
        void BuildRemote()
        {
            var c1 = new Card("Permissions (can only be changed here, on this PC)");
            kScreen = UI.Chk("Allow remote screen sharing sessions (off by default)"); kCam = UI.Chk("Allow remote camera sessions (off by default)");
            nSess = new NumericUpDown { Minimum = 1, Maximum = 240, Value = 30 }; UI.Style(nSess);
            c1.AddWide(kScreen, 32); c1.AddWide(kCam, 32); c1.AddRow("Maximum session length (minutes)", nSess, 36);
            c1.AddWide(Flow(Mk("Save permissions", 1, SavePermissions)), 48);

            var c2 = new Card("Active sessions");
            c2.AddRow("Screen sharing", Val("r_scr"), 30); c2.AddRow("Camera", Val("r_cam"), 30); c2.AddRow("Remote logins", Val("r_log"), 30);
            c2.AddWide(Flow(Mk("Stop all sessions", 2, delegate { Do(delegate { LocalApi.Post("/api/local/session/stop", "{\"type\":\"all\"}"); }); }),
                Mk("Sign out all remote users", 0, delegate { LocalApi.Post("/api/local/revoke", "{}"); })), 48);

            var c3 = new Card("Remote access");
            c3.AddRow("Public URL", Val("r_url"), 30); c3.AddRow("Remote username", Val("r_user"), 30);
            var note = UI.Lbl("Sessions start only when an authenticated remote user explicitly starts one. While a session is active a red banner stays on top of this PC's screen, the tray icon turns red, and a Stop button ends it. Nothing is captured while no session is active.", 9f, false, Theme.Muted);
            note.AutoEllipsis = false; c3.AddWide(note, 70);
            pages[4].Add(c1, c2, c3);
        }

        void SavePermissions()
        {
            if ((kScreen.Checked && !Settings.GetBool("allow_screen")) || (kCam.Checked && !Settings.GetBool("allow_camera")))
                if (MessageBox.Show(this, "Allow authenticated remote users to START screen sharing / camera sessions on this PC?\r\n\r\nEvery session shows a visible banner and can be stopped here.", "Confirm permission", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var d = new Dictionary<string, string>();
            d["allow_screen"] = kScreen.Checked ? "1" : "0"; d["allow_camera"] = kCam.Checked ? "1" : "0"; d["session_minutes"] = ((int)nSess.Value).ToString();
            Settings.Save(d);
            if (!kScreen.Checked || !kCam.Checked) Do(delegate { LocalApi.Post("/api/local/session/stop", "{\"type\":\"" + (!kScreen.Checked && !kCam.Checked ? "all" : !kScreen.Checked ? "screen" : "camera") + "\"}"); });
            Log.Info("Application", "Remote session permissions saved (screen=" + d["allow_screen"] + ", camera=" + d["allow_camera"] + ")");
        }

        // ---- Logs ----------------------------------------------------------------------
        void BuildLogs()
        {
            var c1 = new Card("Filter");
            cbCat = new ComboBox { Width = 160, Margin = new Padding(0, 3, 10, 0) }; UI.Style(cbCat); cbCat.Anchor = AnchorStyles.Left;
            cbCat.Items.AddRange(new object[] { "All", "Application", "Server", "ngrok", "Installation", "Authentication", "Connections", "Errors" }); cbCat.SelectedIndex = 0;
            cbCat.SelectedIndexChanged += delegate { RefreshLogs(); };
            tbSearch = new TextBox { Width = 220, Margin = new Padding(0, 4, 10, 0), BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            tbSearch.TextChanged += delegate { RefreshLogs(); };
            c1.AddWide(Flow(cbCat, tbSearch, Mk("Refresh", 0, RefreshLogs), Mk("Clear display", 0, delegate { clearedAt = DateTime.Now; RefreshLogs(); }), Mk("Export...", 0, ExportLogs)), 52);
            var c2 = new Card("Entries (time, level, category, message)");
            rtLogs = new RichTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = new Font("Consolas", 9f), WordWrap = false };
            c2.AddWide(rtLogs, 470);
            pages[5].Add(c1, c2);
        }

        void RefreshLogs()
        {
            if (rtLogs == null || cbCat.SelectedItem == null) return;
            shown = LogReader.Load((string)cbCat.SelectedItem, tbSearch.Text.Trim(), 400, clearedAt);
            rtLogs.SuspendLayout(); rtLogs.Clear();
            foreach (LogEntry e in shown)
            {
                rtLogs.SelectionStart = rtLogs.TextLength; rtLogs.SelectionLength = 0;
                rtLogs.SelectionColor = (e.Level == "ERROR" || e.Level == "CRITICAL") ? Theme.Bad : e.Level == "WARNING" ? Theme.Warn : Theme.Text;
                rtLogs.AppendText(e.Time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + e.Level.PadRight(8) + e.Cat.PadRight(15) + e.Msg + "\n");
            }
            rtLogs.SelectionStart = rtLogs.TextLength; rtLogs.ScrollToCaret(); rtLogs.ResumeLayout();
        }

        void ExportLogs()
        {
            using (var d = new SaveFileDialog { Filter = "Log file (*.log)|*.log|Text (*.txt)|*.txt", FileName = "PCWatch-logs-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".log" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder(); foreach (LogEntry e in shown) sb.AppendLine(e.Raw);
                File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(false));
                MessageBox.Show(this, "Exported " + shown.Count + " entries (credentials are never written to logs).", "PC Watch");
            }
        }

        // ---- Settings --------------------------------------------------------------------
        Panel PathBox(out TextBox tb)
        {
            var p = new Panel { BackColor = Color.Transparent };
            var t = new TextBox { Dock = DockStyle.Fill, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            var b = new Btn("Browse", 0) { Dock = DockStyle.Right, AutoSize = false, Width = 80 };
            b.Click += delegate
            {
                using (var d = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe|All files|*.*" }) if (d.ShowDialog(this) == DialogResult.OK) t.Text = d.FileName;
            };
            p.Controls.Add(t); p.Controls.Add(b); tb = t; return p;
        }
        static TextBox Tb(bool pw) { var t = new TextBox { UseSystemPasswordChar = pw }; UI.Style(t); return t; }

        void BuildSettings()
        {
            var c1 = new Card("Server and startup");
            nPort = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = 8765 }; UI.Style(nPort);
            cStartup = new ComboBox(); UI.Style(cStartup); cStartup.Items.AddRange(new object[] { "Disabled", "Start server at Windows startup", "Start server and tunnel at Windows startup" });
            kStop = UI.Chk("Stop the server and tunnel when the dashboard is closed");
            c1.AddRow("Server port", nPort, 36); c1.AddRow("Windows startup", cStartup, 36); c1.AddWide(kStop, 32);
            var note = UI.Lbl("Windows startup only starts the hidden background component (HKCU Run entry, visible in Task Manager > Startup). The dashboard window never opens by itself.", 9f, false, Theme.Muted);
            note.AutoEllipsis = false; c1.AddWide(note, 44);

            var c2 = new Card("Tunnel");
            tDom = Tb(false); tToken = Tb(true); tokenState = Val("tokstate");
            c2.AddRow("Reserved ngrok domain (optional)", tDom, 36); c2.AddRow("New authtoken (blank = keep)", tToken, 36); c2.AddRow("Current authtoken", tokenState, 30);

            var c3 = new Card("Paths");
            TextBox t1, t2; Panel p1 = PathBox(out t1), p2 = PathBox(out t2); tPy = t1; tNg = t2; tLogDir = Tb(false);
            c3.AddRow("Installation path", Val("s_inst"), 30); c3.AddRow("Python path", p1, 38); c3.AddRow("ngrok path", p2, 38); c3.AddRow("Log folder (blank = default)", tLogDir, 36);

            var c4 = new Card("Logging");
            cLevel = new ComboBox(); UI.Style(cLevel); cLevel.Items.AddRange(new object[] { "DEBUG", "INFO", "WARNING", "ERROR" });
            nRet = new NumericUpDown { Minimum = 1, Maximum = 365, Value = 14 }; UI.Style(nRet);
            c4.AddRow("Log level", cLevel, 36); c4.AddRow("Keep rotated logs (days)", nRet, 36);

            var c5 = new Card("Remote login");
            tUser = Tb(false); tPw1 = Tb(true); tPw2 = Tb(true);
            c5.AddRow("Username", tUser, 36); c5.AddRow("New password (blank = keep)", tPw1, 36); c5.AddRow("Confirm password", tPw2, 36);
            var pn = UI.Lbl("Passwords need 12+ characters with lowercase, uppercase, a digit and a symbol. They are hashed (PBKDF2-SHA256) by the server and never stored in plain text.", 9f, false, Theme.Muted);
            pn.AutoEllipsis = false; c5.AddWide(pn, 44);

            var c6 = new Card("Appearance");
            cTheme = new ComboBox(); UI.Style(cTheme); cTheme.Items.AddRange(new object[] { "Dark", "Midnight", "Light" });
            c6.AddRow("Dashboard theme (applies on next start)", cTheme, 36);

            var actions = new Card("Apply");
            actions.AddWide(Flow(Mk("Save", 1, SaveSettings), Mk("Cancel", 0, LoadSettings), Mk("Reset to defaults", 2, ResetSettings)), 48);
            pages[6].Add(c1, c2, c3, c4, c5, c6, actions);
        }

        void LoadSettings()
        {
            var s = Settings.All();
            nPort.Value = Math.Max(nPort.Minimum, Math.Min(nPort.Maximum, Settings.GetInt("server_port", 8765)));
            cStartup.SelectedIndex = Math.Max(0, Math.Min(2, Settings.GetInt("startup_mode", 0)));
            kStop.Checked = Settings.GetBool("stop_on_exit");
            tDom.Text = Settings.Get("ngrok_domain"); tToken.Text = "";
            V["tokstate"].Text = File.Exists(Paths.TokenFile) ? "Configured (hidden)" : "Not set";
            V["s_inst"].Text = Paths.InstallDir; tPy.Text = Settings.Get("python_path"); tNg.Text = Settings.Get("ngrok_path"); tLogDir.Text = Settings.Get("log_dir");
            cLevel.SelectedItem = Settings.Get("log_level").ToUpperInvariant(); if (cLevel.SelectedIndex < 0) cLevel.SelectedIndex = 1;
            nRet.Value = Math.Max(nRet.Minimum, Math.Min(nRet.Maximum, Settings.GetInt("log_retention_days", 14)));
            tUser.Text = Settings.Get("remote_user"); tPw1.Text = ""; tPw2.Text = "";
            cTheme.SelectedItem = Settings.Get("theme"); if (cTheme.SelectedIndex < 0) cTheme.SelectedIndex = 0;
            nTunPort.Value = Math.Max(0, Math.Min(65535, Settings.GetInt("tunnel_port", 0)));
            kScreen.Checked = Settings.GetBool("allow_screen"); kCam.Checked = Settings.GetBool("allow_camera");
            nSess.Value = Math.Max(nSess.Minimum, Math.Min(nSess.Maximum, Settings.GetInt("session_minutes", 30)));
        }

        void Warn(string m) { MessageBox.Show(this, m, "Settings not saved", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        void SaveSettings()
        {
            int port = (int)nPort.Value, oldPort = Settings.GetInt("server_port", 8765);
            if (port != oldPort && PortTools.InUse(port)) { Warn("Port " + port + " is already in use by another program."); return; }
            if (!File.Exists(tPy.Text.Trim())) { Warn("The Python path does not exist."); return; }
            if (!File.Exists(tNg.Text.Trim())) { Warn("The ngrok path does not exist."); return; }
            string dom = tDom.Text.Trim();
            if (dom.Length > 0 && !Regex.IsMatch(dom, @"^[A-Za-z0-9][A-Za-z0-9.-]*\.[A-Za-z]{2,}$")) { Warn("The reserved domain must look like name.ngrok-free.app."); return; }
            string logDir = tLogDir.Text.Trim();
            if (logDir.Length > 0)
            {
                try { Directory.CreateDirectory(logDir); string probe = Path.Combine(logDir, ".write-test"); File.WriteAllText(probe, "x"); File.Delete(probe); }
                catch (Exception ex) { Warn("The log folder is not writable: " + ex.Message); return; }
            }
            string tok = tToken.Text.Trim();
            if (tok.Length > 0 && (tok.Length < 20 || !Regex.IsMatch(tok, @"^[A-Za-z0-9_]+$"))) { Warn("That does not look like an ngrok authtoken."); return; }
            string user = tUser.Text.Trim(), pw = tPw1.Text;
            if (pw.Length > 0 || user != Settings.Get("remote_user"))
            {
                string pe = Policy.CheckUser(user);
                if (pe == null && pw.Length == 0) pe = "Changing the username requires entering a new password too.";
                if (pe == null) pe = Policy.CheckPassword(pw, user);
                if (pe == null && pw != tPw2.Text) pe = "The two passwords do not match.";
                if (pe != null) { Warn(pe); return; }
            }
            if (pw.Length > 0) { string ae = AuthTool.Set(user, pw); if (ae != null) { Warn(ae); return; } }
            if (tok.Length > 0) { Secrets.Save(Paths.TokenFile, tok); Log.AddSecret(tok); }
            var d = new Dictionary<string, string>();
            d["server_port"] = port.ToString(); d["startup_mode"] = cStartup.SelectedIndex.ToString(); d["stop_on_exit"] = kStop.Checked ? "1" : "0";
            d["ngrok_domain"] = dom; d["python_path"] = tPy.Text.Trim(); d["ngrok_path"] = tNg.Text.Trim(); d["log_dir"] = logDir;
            d["log_level"] = (string)cLevel.SelectedItem; d["log_retention_days"] = ((int)nRet.Value).ToString(); d["theme"] = (string)cTheme.SelectedItem;
            bool tunnelChanged = dom != Settings.Get("ngrok_domain") || tok.Length > 0;
            Settings.Save(d);
            StartupReg.Apply(cStartup.SelectedIndex);
            var des = Desired.Load();
            if (port != oldPort) { if (Desired.Flag(des, "server")) Do(Desired.StartServer); if (Desired.Flag(des, "tunnel")) Do(Desired.StartTunnel); }
            else if (tunnelChanged && Desired.Flag(des, "tunnel")) Do(Desired.StartTunnel);
            Log.Info("Application", "Settings saved");
            LoadSettings();
            MessageBox.Show(this, "Settings saved." + (port != oldPort ? "\r\nThe server and tunnel are restarting on the new port." : ""), "PC Watch");
        }

        void ResetSettings()
        {
            if (MessageBox.Show(this, "Reset port, startup, logging, tunnel and permission settings to their defaults?\r\n(Paths, credentials and the authtoken are kept.)", "Reset to defaults", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var d = Settings.Defaults();
            foreach (string k in new[] { "python_path", "ngrok_path", "local_key_sha256", "remote_user", "log_dir" }) d.Remove(k);
            int old = Settings.GetInt("server_port", 8765);
            Settings.Save(d); StartupReg.Apply(0);
            if (old != 8765) { var des = Desired.Load(); if (Desired.Flag(des, "server")) Do(Desired.StartServer); if (Desired.Flag(des, "tunnel")) Do(Desired.StartTunnel); }
            LoadSettings();
        }

        // ---- About -----------------------------------------------------------------------
        void BuildAbout()
        {
            var c1 = new Card("PC Watch");
            c1.AddRow("Version", Val("a_ver"), 30); c1.AddRow("Installed in", Val("a_dir"), 30); c1.AddRow("Python", Val("a_py"), 30); c1.AddRow("ngrok", Val("a_ng"), 30);
            c1.AddRow("Logs", Val("a_logs"), 30); c1.AddRow("Built by", Val("a_by"), 30);
            V["a_ver"].Text = Program.Version; V["a_dir"].Text = Paths.InstallDir; V["a_logs"].Text = Log.Dir; V["a_by"].Text = "Designed by MD";
            V["a_py"].Text = "checking..."; V["a_ng"].Text = "checking...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string o1, o2;
                try { ProcTools.Run(Settings.Get("python_path"), "--version", 8000, out o1); } catch { o1 = "not found"; }
                try { ProcTools.Run(Settings.Get("ngrok_path"), "version", 8000, out o2); } catch { o2 = "not found"; }
                try { BeginInvoke(new Action(delegate { V["a_py"].Text = o1.Trim() + "  (" + Settings.Get("python_path") + ")"; V["a_ng"].Text = o2.Trim(); })); } catch { }
            });
            var c2 = new Card("Maintenance");
            c2.AddWide(Flow(Mk("Open install folder", 0, delegate { Process.Start("explorer.exe", "\"" + Paths.InstallDir + "\""); }),
                Mk("Open logs folder", 0, delegate { Process.Start("explorer.exe", "\"" + Log.Dir + "\""); }),
                Mk("Repair", 0, delegate { Maint.Launch("--repair", this); }),
                Mk("Uninstall", 2, delegate { Maint.Launch("--uninstall", this); })), 92);
            var c3 = new Card("How it works");
            var t = UI.Lbl("PCWatch.exe is only the control panel. A hidden background component (tray icon) runs the Python server and the ngrok tunnel, so they keep working when this window is closed or hidden. The remote dashboard is served at your ngrok HTTPS URL behind a login.", 9.5f, false, Theme.Muted);
            t.AutoEllipsis = false; c3.AddWide(t, 80);
            pages[7].Add(c1, c2, c3);
        }

        // ---- live data ----------------------------------------------------------------------
        void PollLoop()
        {
            int n = 0;
            while (!closing)
            {
                try
                {
                    Snapshot s = Collect(n++ % 2 == 0);
                    if (!closing && IsHandleCreated) BeginInvoke(new Action<Snapshot>(ApplySnapshot), s);
                }
                catch (Exception ex) { if (!closing) Log.Warn("Application", "Poll error: " + ex.Message); }
                for (int i = 0; i < 15 && !closing; i++) Thread.Sleep(100);
            }
        }

        Snapshot Collect(bool deep)
        {
            var s = new Snapshot(); var st = Ini.Read(StatusFile.FilePath); long hb;
            s.HostAlive = long.TryParse(G(st, "heartbeat_unix"), out hb) && Math.Abs(StatusFile.UnixNow() - hb) < 20;
            if (s.HostAlive)
            {
                s.ServerState = G(st, "server_state"); s.TunnelState = G(st, "tunnel_state");
                s.ServerError = G(st, "server_error"); s.TunnelError = G(st, "tunnel_error"); s.TunnelUrl = G(st, "tunnel_url");
                int.TryParse(G(st, "server_pid"), out s.ServerPid); int.TryParse(G(st, "tunnel_pid"), out s.TunnelPid);
                long.TryParse(G(st, "server_started_unix"), out s.ServerStartedUnix);
            }
            s.Cpu = SysInfo.Cpu(); SysInfo.Ram(out s.RamTotal, out s.RamAvail); s.SysUptime = SysInfo.Uptime();
            var ips = SysInfo.LocalIPs(); s.Ip = ips.Count > 0 ? string.Join(", ", ips.ToArray()) : "-";
            var lc = Ini.Read(Path.Combine(Paths.RunDir, "last_connection.ini"));
            if (lc.Count > 0) s.LastConn = G(lc, "time") + "  (" + G(lc, "user") + " from " + G(lc, "ip") + ")";
            s.ScreenOn = snap.ScreenOn; s.CamOn = snap.CamOn; s.ScreenInfo = snap.ScreenInfo; s.CamInfo = snap.CamInfo; s.Logins = snap.Logins;
            if (deep)
            {
                s.ScreenOn = false; s.CamOn = false; s.ScreenInfo = ""; s.CamInfo = "";
                if (s.ServerState == "Running")
                {
                    var d = LocalApi.Get("/api/local/status");
                    if (d != null)
                    {
                        int lg; int.TryParse(Json.Str(d, "logins"), out lg); s.Logins = lg;
                        var m = d.ContainsKey("media") ? d["media"] as Dictionary<string, object> : null;
                        if (m != null)
                        {
                            var sc = m.ContainsKey("screen") ? m["screen"] as Dictionary<string, object> : null;
                            var cm = m.ContainsKey("camera") ? m["camera"] as Dictionary<string, object> : null;
                            if (sc != null) { s.ScreenOn = true; s.ScreenInfo = "by " + Json.Str(sc, "by") + " from " + Json.Str(sc, "ip"); }
                            if (cm != null) { s.CamOn = true; s.CamInfo = "by " + Json.Str(cm, "by") + " from " + Json.Str(cm, "ip"); }
                        }
                    }
                }
            }
            var recent = LogReader.Load("All", "", 8, DateTime.MinValue);
            s.Recent = recent.Select(delegate(LogEntry e) { return e.Time.ToString("HH:mm:ss") + "  " + e.Level.PadRight(8) + e.Msg; }).ToArray();
            return s;
        }

        void SetBadge(string id, string state, string label) { Badge b; if (B.TryGetValue(id, out b)) b.SetState(state, label); }

        void ApplySnapshot(Snapshot s)
        {
            if (closing || IsDisposed) return;
            snap = s;
            string ss = s.HostAlive ? s.ServerState : "Unknown", ts = s.HostAlive ? s.TunnelState : "Unknown";
            SetBadge("server", ss, s.HostAlive ? ss : "Background off"); SetBadge("server_b", ss, s.HostAlive ? ss : "Background off");
            SetBadge("tunnel", ts, s.HostAlive ? ts : "Background off"); SetBadge("tunnel_b", ts, s.HostAlive ? ts : "Background off");
            SetBadge("host", s.HostAlive ? "Running" : "Unknown", s.HostAlive ? "Installed - running" : "Not running"); SetBadge("host2", s.HostAlive ? "Running" : "Unknown", s.HostAlive ? "Running" : "Not running");
            bool avail = ss == "Running" && ts == "Running" && s.TunnelUrl.Length > 0;
            string endp = !s.HostAlive ? "Unknown" : avail ? "Available" : ss != "Running" ? "Unavailable - server " + ss.ToLowerInvariant() : "Unavailable - tunnel " + ts.ToLowerInvariant();
            SetBadge("endpoint", !s.HostAlive ? "Unknown" : avail ? "Running" : "Stopped", endp);

            int port = Settings.GetInt("server_port", 8765);
            V["pc"].Text = Environment.MachineName; V["ip"].Text = s.Ip; V["port"].Text = port.ToString();
            V["url"].Text = avail ? s.TunnelUrl : (ts == "Running" ? s.TunnelUrl : "-");
            ulong used = s.RamTotal - s.RamAvail; double rp = s.RamTotal > 0 ? used * 100.0 / s.RamTotal : 0;
            mCpu.Set(s.Cpu, s.Cpu.ToString("0") + " %"); mCpu2.Set(s.Cpu, s.Cpu.ToString("0") + " %");
            string rt = SysInfo.Gb(used) + " / " + SysInfo.Gb(s.RamTotal); mRam.Set(rp, rt); mRam2.Set(rp, rt);
            bool up = ss == "Running" && s.ServerStartedUnix > 0;
            string upt = up ? SysInfo.Span(DateTime.UtcNow - StatusFile.FromUnix(s.ServerStartedUnix)) : "-";
            V["up"].Text = upt; V["last"].Text = s.LastConn;
            rtRecent.Text = string.Join("\n", s.Recent); rtRecent.SelectionStart = rtRecent.TextLength; rtRecent.ScrollToCaret();

            V["spid"].Text = s.ServerPid > 0 ? s.ServerPid.ToString() : "-"; V["sup"].Text = upt; V["sport"].Text = "127.0.0.1:" + port + " (only the local tunnel can reach it)";
            V["sendp"].Text = endp; V["serr"].Text = Help(s.ServerError);
            V["tpid"].Text = s.TunnelPid > 0 ? s.TunnelPid.ToString() : "-"; tbUrl.Text = s.TunnelUrl;
            int tp = Settings.GetInt("tunnel_port", 0); V["tfwd"].Text = "127.0.0.1:" + (tp > 0 ? tp : port);
            V["ttok"].Text = File.Exists(Paths.TokenFile) ? "Configured (hidden)" : "NOT SET - the tunnel cannot start"; V["terr"].Text = Help(s.TunnelError);
            SetBtn("srvStart", ss == "Stopped" || ss == "Error" || ss == "Unknown"); SetBtn("srvStop", ss != "Stopped" && ss != "Unknown"); SetBtn("srvRestart", ss != "Stopped" && ss != "Unknown");
            SetBtn("tunStart", ts == "Stopped" || ts == "Error" || ts == "Unknown"); SetBtn("tunStop", ts != "Stopped" && ts != "Unknown"); SetBtn("tunRestart", ts != "Stopped" && ts != "Unknown");

            V["i_ramt"].Text = SysInfo.Gb(s.RamTotal); V["i_rama"].Text = SysInfo.Gb(s.RamAvail); V["i_up"].Text = SysInfo.Span(s.SysUptime);
            V["i_ips"].Text = s.Ip; V["i_port"].Text = port.ToString();

            V["r_scr"].Text = s.ScreenOn ? "ACTIVE " + s.ScreenInfo : "Inactive"; V["r_cam"].Text = s.CamOn ? "ACTIVE " + s.CamInfo : "Inactive";
            V["r_scr"].ForeColor = s.ScreenOn ? Theme.Bad : Theme.Text; V["r_cam"].ForeColor = s.CamOn ? Theme.Bad : Theme.Text;
            V["r_log"].Text = s.Logins.ToString(); V["r_url"].Text = avail ? s.TunnelUrl : "Not available (tunnel/server not running)"; V["r_user"].Text = Settings.Get("remote_user");
        }
        void SetBtn(string id, bool en) { Btn b; if (Bt.TryGetValue(id, out b)) b.Enabled = en; }
    }

    // ========================================================================
    //  INSTALLER ENGINE (runs inside setup.exe, and inside the --repair copy)
    // ========================================================================
    internal sealed class InstallOptions
    {
        public string Dir = @"C:\Program Files\PC Watch";
        public int Port = 8765, StartupMode;
        public string User = "", Password = "", Token = "", Domain = "", LogDir = "";
        public bool DesktopShortcut, Repair;
    }

    internal sealed class InstallException : Exception
    {
        public readonly string Help;
        public InstallException(string message, string help) : base(message) { Help = help; }
    }

    internal static class Elevate
    {
        public static string Quote(string[] a) { return string.Join(" ", a.Select(delegate(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }).ToArray()); }
        // Returns true when an elevated copy was started and this process must exit
        public static bool Relaunch(string[] args)
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, Quote(args)) { UseShellExecute = true, Verb = "runas" }); }
            catch (System.ComponentModel.Win32Exception) { MessageBox.Show("PC Watch setup needs administrator rights to install into Program Files. Start it again and accept the Windows prompt.", "PC Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            return true;
        }
    }

    internal static class Maint
    {
        // Copies this exe to %TEMP% and starts the copy elevated, so the install folder is not locked while it is modified
        public static void Launch(string flag, IWin32Window owner)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "PCWatch-maint-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            File.Copy(Application.ExecutablePath, tmp, true);
            try { Process.Start(new ProcessStartInfo(tmp, flag + " \"" + Paths.InstallDir + "\"") { UseShellExecute = true, Verb = "runas" }); }
            catch (System.ComponentModel.Win32Exception) { return; }   // UAC declined
            Application.Exit();
        }
        public static bool InsideInstallDir() { return Application.ExecutablePath.StartsWith(Paths.InstallDir + "\\", StringComparison.OrdinalIgnoreCase); }

        // Stops ONLY PC Watch's own processes (identified by pid file + full image path, or by living inside the install folder)
        public static void StopEverything(string dir, Action<string> log)
        {
            try { var ev = EventWaitHandle.OpenExisting("Local\\PCWatch.Host.Exit"); ev.Set(); ev.Dispose(); log("Asked the background component to exit (it stops the server and tunnel gracefully)..."); } catch { }
            for (int i = 0; i < 75 && HostLauncher.IsRunning(); i++) Thread.Sleep(200);
            foreach (string name in new[] { "server", "tunnel" })
            {
                try
                {
                    string pf = Path.Combine(dir, "Config", "run", name + ".pid");
                    if (!File.Exists(pf)) continue;
                    int pid; string[] a = File.ReadAllText(pf).Trim().Split('|');
                    if (a.Length == 2 && int.TryParse(a[0], out pid))
                    {
                        string path = ProcTools.PathOf(pid), want = Settings.Get(name == "server" ? "python_path" : "ngrok_path");
                        if (path != null && ProcTools.SamePath(path, want)) { log("Stopping " + name + " process " + pid); ProcTools.KillTree(pid); }
                    }
                    File.Delete(pf);
                }
                catch { }
            }
            foreach (Process p in ProcTools.InDir(dir)) { try { log("Stopping " + p.ProcessName + " (" + p.Id + ")"); ProcTools.KillTree(p.Id); } catch { } }
            Thread.Sleep(600);
        }
    }

    internal static class Shortcut
    {
        public static void Create(string lnk, string target, string args, string workDir, string desc, int windowStyle)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            object s = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
            Type st = s.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, s, new object[] { target });
            st.InvokeMember("Arguments", BindingFlags.SetProperty, null, s, new object[] { args ?? "" });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, s, new object[] { workDir });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, s, new object[] { desc });
            st.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, s, new object[] { windowStyle });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, s, new object[] { target + ",0" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, s, null);
        }
        public static string StartMenuDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "PC Watch"); } }
        public static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "PC Watch.lnk"); } }
    }

    internal static class SourceProvider
    {
        // setup.exe carries PCWatch.cs as an embedded resource (see build command); fall back to files for repair
        public static string Read(out string origin)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PCWatch.cs"))
            {
                if (s != null) using (var sr = new StreamReader(s, Encoding.UTF8)) { origin = "the copy embedded in this EXE"; return sr.ReadToEnd(); }
            }
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            foreach (string c in new[] { Path.Combine(exeDir, "PCWatch.cs"), Path.Combine(exeDir, "Source", "PCWatch.cs"), Path.Combine(Paths.SourceDir, "PCWatch.cs") })
                if (File.Exists(c)) { origin = c; return File.ReadAllText(c, Encoding.UTF8); }
            throw new InstallException("PCWatch.cs was not found, so PCWatch.exe cannot be generated.",
                "Build setup.exe with /resource:PCWatch.cs,PCWatch.cs (see the build command), or put PCWatch.cs next to setup.exe.");
        }
    }

    internal static class Deps
    {
        public const string PythonVersion = "3.12.8";
        public static string PythonUrl { get { return "https://www.python.org/ftp/python/" + PythonVersion + (Environment.Is64BitOperatingSystem ? "/python-" + PythonVersion + "-amd64.exe" : "/python-" + PythonVersion + ".exe"); } }
        public static string NgrokUrl { get { return "https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-windows-" + (Environment.Is64BitOperatingSystem ? "amd64" : "386") + ".zip"; } }

        public static int NetFxRelease()
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full")) return k == null ? 0 : Convert.ToInt32(k.GetValue("Release")); } catch { return 0; }
        }
        public static string Csc()
        {
            string p = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "csc.exe");
            return File.Exists(p) ? p : null;
        }
        public static bool CheckPython(string exe, out string ver)
        {
            ver = "";
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe) || exe.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) return false;   // WindowsApps = Store stub
            string o; int c = ProcTools.Run(exe, "-c \"import sys,ssl;print('%d.%d.%d'%sys.version_info[:3])\"", 25000, out o);
            var m = Regex.Match(o, @"(\d+)\.(\d+)\.(\d+)");
            if (c != 0 || !m.Success) return false;
            if (int.Parse(m.Groups[1].Value) < 3 || (int.Parse(m.Groups[1].Value) == 3 && int.Parse(m.Groups[2].Value) < 9)) return false;
            string o2; if (ProcTools.Run(exe, "-m pip --version", 25000, out o2) != 0) return false;
            ver = m.Value; return true;
        }
        public static string FindPython(out string ver)
        {
            ver = "";
            var cands = new List<string>();
            cands.Add(Settings.Get("python_path")); cands.Add(Path.Combine(Paths.RuntimeDir, "Python", "python.exe"));
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) { try { if (d.Trim().Length > 0) cands.Add(Path.Combine(d.Trim(), "python.exe")); } catch { } }
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var core = hive.OpenSubKey(@"SOFTWARE\Python\PythonCore"))
                        if (core != null) foreach (string v in core.GetSubKeyNames())
                            using (var ip = core.OpenSubKey(v + @"\InstallPath")) { if (ip != null) cands.Add(Path.Combine(Convert.ToString(ip.GetValue("")) ?? "", "python.exe")); }
                }
                catch { }
            }
            foreach (string c in cands.Distinct()) if (CheckPython(c, out ver)) return c;
            return null;
        }
        public static bool CheckNgrok(string exe, out string ver)
        {
            ver = ""; if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
            string o; int c = ProcTools.Run(exe, "version", 20000, out o);
            var m = Regex.Match(o, @"version\s+(\d+)\.(\d+)\.(\d+)");
            if (c != 0 || !m.Success || int.Parse(m.Groups[1].Value) < 3) return false;
            ver = m.Groups[1].Value + "." + m.Groups[2].Value + "." + m.Groups[3].Value; return true;
        }
        public static string FindNgrok(out string ver)
        {
            ver = "";
            var cands = new List<string>(); cands.Add(Settings.Get("ngrok_path")); cands.Add(Path.Combine(Paths.ToolsDir, "ngrok.exe"));
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) { try { if (d.Trim().Length > 0) cands.Add(Path.Combine(d.Trim(), "ngrok.exe")); } catch { } }
            foreach (string c in cands.Distinct()) if (CheckNgrok(c, out ver)) return c;
            return null;
        }
        public static bool Online()
        {
            string t; return Http.Send("GET", "https://www.python.org/", null, null, 10000, false, out t) > 0;
        }
        public static void Download(string url, string dest, Action<int> pct)
        {
            var req = (HttpWebRequest)WebRequest.Create(url); req.UserAgent = "PCWatch-Setup/" + Program.Version; req.Timeout = 30000; req.ReadWriteTimeout = 30000;
            string part = dest + ".part";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var fs = File.Create(part))
            {
                long total = resp.ContentLength, got = 0; var buf = new byte[81920]; int n, last = -1;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, n); got += n;
                    if (total > 0) { int p = (int)(got * 100 / total); if (p != last) { last = p; pct(p); } }
                }
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
        }
        // Authenticode check through PowerShell (present on every Windows 10/11)
        public static string Signature(string file, out string subject)
        {
            string o; ProcTools.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"$s = Get-AuthenticodeSignature -LiteralPath '" + file.Replace("'", "''") +
                "'; Write-Output ($s.Status.ToString() + '|' + $s.SignerCertificate.Subject)\"", 90000, out o);
            string line = o.Trim().Split('\n').Last().Trim(); int i = line.IndexOf('|');
            subject = i >= 0 ? line.Substring(i + 1) : ""; return i >= 0 ? line.Substring(0, i) : line;
        }
    }

    internal sealed class InstallEngine
    {
        readonly InstallOptions o; readonly Action<int, string> prog; readonly Action<string, string> logf;
        public readonly List<string> Warnings = new List<string>();
        public string PythonPath = "", NgrokPath = "", PythonVer = "", NgrokVer = "";
        public InstallEngine(InstallOptions o, Action<int, string> prog, Action<string, string> logf) { this.o = o; this.prog = prog; this.logf = logf; }
        void L(string lvl, string m) { Log.Write(lvl, "Installation", m); logf(lvl, m); }
        void P(int pct, string m) { prog(pct, m); L("INFO", m); }
        void W(string m) { Warnings.Add(m); L("WARNING", m); }

        public void Run()
        {
            Paths.Init(o.Dir);
            Log.OverrideDir = string.IsNullOrWhiteSpace(o.LogDir) ? Paths.DefaultLogDir : o.LogDir;
            L("INFO", "=== PC Watch " + Program.Version + (o.Repair ? " repair" : " installation") + " started ===");

            P(2, "Checking prerequisites...");
            if (!SysInfo.IsAdmin()) throw new InstallException("Setup is not running as administrator.", "Close setup, start it again and accept the Windows UAC prompt.");
            if (Deps.NetFxRelease() < 461808) throw new InstallException(".NET Framework 4.7.2 or newer is required to build PCWatch.exe.", "Run Windows Update (Windows 10 1803+ and Windows 11 include it) or install .NET Framework 4.8 from microsoft.com, then run setup again.");
            string csc = Deps.Csc();
            if (csc == null) throw new InstallException("The Windows C# compiler (csc.exe) was not found in " + RuntimeEnvironment.GetRuntimeDirectory(), "Repair .NET Framework 4.x (Windows Features) and retry.");
            string origin; string src = SourceProvider.Read(out origin);
            L("INFO", "PCWatch.cs taken from " + origin);
            if (src.IndexOf("DASHBOARD_BUILD", StringComparison.Ordinal) < 0) throw new InstallException("PCWatch.cs looks incomplete.", "Use the original, complete PCWatch.cs file.");

            P(6, "Stopping running PC Watch components (if any)...");
            Maint.StopEverything(o.Dir, delegate(string m) { L("INFO", m); });

            P(9, "Creating folders and permissions...");
            foreach (string d in new[] { Paths.InstallDir, Paths.ServerDir, Paths.ToolsDir, Paths.ConfigDir, Paths.RunDir, Paths.SourceDir, Paths.RuntimeDir, Log.OverrideDir }) Directory.CreateDirectory(d);
            string me = WindowsIdentity.GetCurrent().Name;
            foreach (string d in new[] { Paths.ConfigDir, Paths.DefaultLogDir })
            {
                Directory.CreateDirectory(d);
                string o1; int c = ProcTools.Run("icacls.exe", "\"" + d + "\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F \"" + me + "\":(OI)(CI)M", 60000, out o1);
                if (c != 0) throw new InstallException("Could not set folder permissions on " + d + ": " + o1.Trim(), "Make sure the install folder is on an NTFS drive and you have administrator rights.");
            }

            InstallPython();
            InstallServer();
            InstallNgrok();
            Configure();
            Build(csc, src);
            Finalize_();
            P(100, "Installation complete.");
        }

        void InstallPython()
        {
            string ver; string py = Deps.FindPython(out ver);
            if (py != null) { P(14, "Python " + ver + " found at " + py + " - not reinstalling."); PythonPath = py; PythonVer = ver; return; }
            string inst = Path.Combine(Paths.RuntimeDir, "python-installer.exe");
            P(12, "Downloading Python " + Deps.PythonVersion + " from python.org...");
            try { Deps.Download(Deps.PythonUrl, inst, delegate(int p) { prog(12 + p * 16 / 100, "Downloading Python... " + p + "%"); }); }
            catch (Exception ex) { throw new InstallException("Python download failed: " + ex.Message, "Check your internet connection / proxy, then retry. Or install Python 3.9+ yourself (python.org) and run setup again."); }
            P(28, "Verifying the Python installer signature...");
            string subj, status = Deps.Signature(inst, out subj);
            L("INFO", "Signature: " + status + " " + subj);
            if (status != "Valid" || subj.IndexOf("Python Software Foundation", StringComparison.OrdinalIgnoreCase) < 0)
            { try { File.Delete(inst); } catch { } throw new InstallException("The downloaded Python installer failed signature verification (" + status + ").", "The download may be corrupted or intercepted. Retry on a trusted network."); }
            P(30, "Installing Python (private copy inside the PC Watch folder, PATH untouched)...");
            string target = Path.Combine(Paths.RuntimeDir, "Python"), outp;
            int code = ProcTools.Run(inst, "/quiet InstallAllUsers=0 PrependPath=0 Include_launcher=0 Include_test=0 Include_doc=0 AssociateFiles=0 Shortcuts=0 TargetDir=\"" + target + "\"", 900000, out outp);
            if (code != 0) throw new InstallException("The Python installer failed (exit code " + code + ").\r\n" + outp.Trim(), "Check %TEMP% for Python installer logs; ensure no other installer is running and Windows is up to date.");
            py = Path.Combine(target, "python.exe");
            if (!Deps.CheckPython(py, out ver)) throw new InstallException("Python was installed but does not run correctly.", "Retry; if it persists install Python 3.9+ manually and run setup again.");
            PythonPath = py; PythonVer = ver; L("INFO", "Python " + ver + " installed at " + py);
        }

        void Pip(string package, bool required, int pct)
        {
            P(pct, "Installing Python package: " + package + (required ? "" : " (optional)"));
            string lib = Path.Combine(Paths.ServerDir, "lib"); string last = "";
            var env = new Dictionary<string, string>(); env["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var tail = new StringBuilder();
                int c = ProcTools.RunStream(PythonPath, "-m pip install --no-warn-script-location --upgrade --target \"" + lib + "\" " + package, 900000,
                    delegate(string line) { if (line.Trim().Length > 0) { tail.AppendLine(line); if (tail.Length > 3000) tail.Remove(0, tail.Length - 3000); } }, env, null, Paths.ServerDir);
                last = tail.ToString();
                if (c == 0) { L("INFO", package + " installed"); return; }
                L("WARNING", package + " attempt " + attempt + " failed (exit " + c + ")");
            }
            if (required) throw new InstallException("pip could not install '" + package + "'.\r\n" + last.Trim(), "Check the internet connection and that no proxy blocks pypi.org, then retry.");
            W("Optional package '" + package + "' could not be installed: the related feature (screen/camera) will be unavailable. Last output: " + last.Trim().Replace("\r\n", " | "));
        }

        void InstallServer()
        {
            P(40, "Writing the Python server files...");
            File.WriteAllText(Path.Combine(Paths.ServerDir, "server.py"), ServerFiles.ServerPy, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Paths.ServerDir, "web.html"), ServerFiles.WebHtml, new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(Paths.ServerDir, "lib"));
            Pip("psutil", true, 44); Pip("mss", false, 52); Pip("Pillow", false, 56); Pip("opencv-python-headless", false, 60);
            P(64, "Checking the server installation...");
            string o1; var env = new Dictionary<string, string>(); env["PYTHONPATH"] = Path.Combine(Paths.ServerDir, "lib");
            int c = ProcTools.Run(PythonPath, "\"" + Path.Combine(Paths.ServerDir, "server.py") + "\" --check --config \"" + Paths.SettingsFile + "\"", 90000, out o1, env, null, Paths.ServerDir);
            var caps = Json.Obj(o1.Trim().Split('\n').Last().Trim());
            if (c != 0 || caps == null) throw new InstallException("The server self-check failed:\r\n" + o1.Trim(), "Retry the installation; if it persists, run setup again after deleting the Server\\lib folder.");
            if (Json.Str(caps, "psutil") != "True") throw new InstallException("psutil is not importable after installation.", "Retry the installation.");
            if (Json.Str(caps, "mss") != "True" || Json.Str(caps, "pillow") != "True") W("Screen sharing packages are not fully available (mss/Pillow).");
            if (Json.Str(caps, "opencv") != "True") W("Camera support (opencv) is not available.");
        }

        void InstallNgrok()
        {
            string ver; string ng = Deps.FindNgrok(out ver);
            if (ng != null) { P(74, "ngrok " + ver + " found at " + ng + " - not reinstalling."); NgrokPath = ng; NgrokVer = ver; return; }
            string zip = Path.Combine(Paths.ToolsDir, "ngrok.zip"), exe = Path.Combine(Paths.ToolsDir, "ngrok.exe");
            P(66, "Downloading ngrok from ngrok.com...");
            try { Deps.Download(Deps.NgrokUrl, zip, delegate(int p) { prog(66 + p * 8 / 100, "Downloading ngrok... " + p + "%"); }); }
            catch (Exception ex) { throw new InstallException("ngrok download failed: " + ex.Message, "Check your internet connection, or download ngrok yourself from ngrok.com/download and place ngrok.exe in " + Paths.ToolsDir + ", then run setup again."); }
            try
            {
                using (var za = ZipFile.OpenRead(zip))
                {
                    var e = za.Entries.FirstOrDefault(delegate(ZipArchiveEntry x) { return string.Equals(x.Name, "ngrok.exe", StringComparison.OrdinalIgnoreCase); });
                    if (e == null) throw new InvalidDataException("ngrok.exe is not inside the downloaded archive");
                    e.ExtractToFile(exe, true);
                }
            }
            catch (Exception ex) { throw new InstallException("Could not unpack ngrok: " + ex.Message, "Retry; the download may have been interrupted."); }
            try { File.Delete(zip); } catch { }
            string subj, status = Deps.Signature(exe, out subj);
            L("INFO", "ngrok.exe signature: " + status + " " + subj);
            if (status == "NotSigned") W("ngrok.exe is not Authenticode-signed; it was verified only by running it and reading its version.");
            else if (status != "Valid") { try { File.Delete(exe); } catch { } throw new InstallException("ngrok.exe failed signature verification (" + status + ").", "The download may be corrupted or intercepted. Retry on a trusted network."); }
            if (!Deps.CheckNgrok(exe, out ver)) throw new InstallException("ngrok was downloaded but does not run.", "Check antivirus quarantine for Tools\\ngrok.exe and retry.");
            NgrokPath = exe; NgrokVer = ver; L("INFO", "ngrok " + ver + " ready");
        }

        void Configure()
        {
            P(76, "Writing configuration and credentials...");
            var d = new Dictionary<string, string>();
            d["server_port"] = o.Port.ToString(); d["startup_mode"] = o.StartupMode.ToString(); d["log_dir"] = string.IsNullOrWhiteSpace(o.LogDir) || ProcTools.SamePath(o.LogDir, Paths.DefaultLogDir) ? "" : o.LogDir;
            d["python_path"] = PythonPath; d["ngrok_path"] = NgrokPath; d["ngrok_domain"] = o.Domain; d["version"] = Program.Version;
            if (!string.IsNullOrEmpty(o.User)) d["remote_user"] = o.User;
            string key = Secrets.RandomHex(32);
            Secrets.Save(Paths.KeyFile, key); d["local_key_sha256"] = Secrets.Sha256Hex(key);
            Settings.Save(d);
            if (!string.IsNullOrEmpty(o.Token)) { Secrets.Save(Paths.TokenFile, o.Token); Log.AddSecret(o.Token); }
            if (!File.Exists(Paths.TokenFile)) W("No ngrok authtoken is configured yet: the tunnel cannot start until you set one (Tunnel > Set authtoken).");
            if (!string.IsNullOrEmpty(o.Password))
            {
                string err = AuthTool.Set(o.User, o.Password);
                if (err != null) throw new InstallException(err, "Choose a password with 12+ characters (upper, lower, digit, symbol).");
            }
            if (!File.Exists(Path.Combine(Paths.ConfigDir, "auth.json"))) throw new InstallException("No remote login credentials exist.", "Go back and enter a username and a strong password.");
            if (!File.Exists(Desired.FilePath)) Desired.Set("server", "0", "tunnel", "0");
        }

        void Build(string csc, string src)
        {
            P(80, "Generating PCWatch.exe from PCWatch.cs (Windows C# compiler)...");
            string srcPath = Path.Combine(Paths.SourceDir, "PCWatch.cs"), ico = Path.Combine(Paths.SourceDir, "pcwatch.ico"), tmp = Path.Combine(Paths.InstallDir, "PCWatch.new.exe");
            File.WriteAllText(srcPath, src, new UTF8Encoding(true));
            File.WriteAllBytes(ico, IconFactory.IcoBytes());
            if (File.Exists(tmp)) File.Delete(tmp);
            string args = "/nologo /target:winexe /optimize+ /platform:anycpu /define:DASHBOARD_BUILD /out:\"" + tmp + "\" /win32icon:\"" + ico + "\" " +
                "/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll /r:System.Web.Extensions.dll " +
                "/r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll \"" + srcPath + "\"";
            L("INFO", "csc " + args);
            var sb = new StringBuilder();
            int code = ProcTools.RunStream(csc, args, 300000, delegate(string line) { sb.AppendLine(line); L(line.Contains("error") ? "ERROR" : "INFO", line); }, null, null, Paths.InstallDir);
            if (code != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length < 10000)
                throw new InstallException("The compiler could not generate PCWatch.exe (exit code " + code + ").\r\n\r\n" + sb.ToString().Trim(),
                    "Fix the compiler errors above (they are also in the Installation log). If you did not edit PCWatch.cs, repair .NET Framework and retry.");
            P(92, "Testing the generated PCWatch.exe...");
            string o1; int st = ProcTools.Run(tmp, "--selftest", 30000, out o1);
            if (st != 42) { try { File.Delete(tmp); } catch { } throw new InstallException("The generated PCWatch.exe did not pass its self-test (exit code " + st + ").", "Antivirus software may have blocked or modified the new EXE. Allow it and retry."); }
            string exe = Paths.ExePath;
            for (int i = 0; i < 10; i++) { try { if (File.Exists(exe)) File.Delete(exe); File.Move(tmp, exe); break; } catch (IOException) { if (i == 9) throw new InstallException("PCWatch.exe is locked by another program.", "Close PC Watch / antivirus scanners and retry."); Thread.Sleep(500); } }
            L("INFO", "PCWatch.exe generated (" + new FileInfo(exe).Length / 1024 + " KB)");
        }

        void Finalize_()
        {
            P(94, "Creating shortcuts and Windows registrations...");
            string exe = Paths.ExePath;
            Shortcut.Create(Path.Combine(Shortcut.StartMenuDir, "PC Watch.lnk"), exe, "", Paths.InstallDir, "PC Watch dashboard", 1);
            Shortcut.Create(Path.Combine(Shortcut.StartMenuDir, "Uninstall PC Watch.lnk"), exe, "--uninstall", Paths.InstallDir, "Uninstall PC Watch", 1);
            Shortcut.Create(Path.Combine(Paths.InstallDir, "PC Watch Background.lnk"), exe, "--background", Paths.InstallDir, "PC Watch background component (no window)", 7);
            if (o.DesktopShortcut) Shortcut.Create(Shortcut.DesktopLnk, exe, "", Paths.InstallDir, "PC Watch dashboard", 1);
            using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PCWatch"))
            {
                k.SetValue("DisplayName", "PC Watch"); k.SetValue("DisplayVersion", Program.Version); k.SetValue("Publisher", "PC Watch");
                k.SetValue("InstallLocation", Paths.InstallDir); k.SetValue("DisplayIcon", exe); k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            }
            StartupReg.Apply(o.StartupMode);
            foreach (string must in new[] { exe, Path.Combine(Paths.ServerDir, "server.py"), Path.Combine(Paths.ServerDir, "web.html"), Paths.SettingsFile, PythonPath, NgrokPath })
                if (!File.Exists(must)) throw new InstallException("Final verification failed: missing " + must, "Run setup again (repair).");
            L("INFO", "Installed. Python " + PythonVer + ", ngrok " + NgrokVer + ", " + Warnings.Count + " warning(s).");
        }
    }

    // ========================================================================
    //  SETUP WIZARD (setup.exe) - 6 steps
    // ========================================================================
    internal sealed class SetupWizard : Form
    {
        static readonly string[] Steps = { "Welcome", "Install location", "Dependencies", "Configuration", "Installation", "Finish" };
        readonly InstallOptions o = new InstallOptions();
        readonly Panel[] pg = new Panel[6]; readonly Label[] stepLbl = new Label[6];
        Label title, lDisk, lExisting, lStatus, lDone, lHint; Btn bBack, bNext, bCancel;
        TextBox tDir, tUser, tPw, tPw2, tTok, tDom, tLog; NumericUpDown nPort; ComboBox cStart; CheckBox kDesk, kStartNow, kLaunch;
        ListView lv; ProgressBar bar; RichTextBox rt, rtDone;
        int cur; bool installing, existing, hasAuth, scanOk; InstallEngine engine;

        static string ExistingDir()
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PCWatch")) return k == null ? null : Convert.ToString(k.GetValue("InstallLocation")); } catch { return null; }
        }

        public SetupWizard(bool repair, string dir)
        {
            string found = dir ?? ExistingDir();
            if (!string.IsNullOrEmpty(found) && File.Exists(Path.Combine(found, "Config", "settings.ini")))
            {
                Paths.Init(found); existing = true; o.Repair = true; o.Dir = Paths.InstallDir;
                o.Port = Settings.GetInt("server_port", 8765); o.StartupMode = Settings.GetInt("startup_mode", 0); o.Domain = Settings.Get("ngrok_domain");
                o.User = Settings.Get("remote_user"); o.LogDir = Settings.Get("log_dir"); hasAuth = File.Exists(Path.Combine(found, "Config", "auth.json"));
            }
            Text = "PC Watch Setup"; Icon = IconFactory.MakeIcon(Theme.Accent); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(830, 590);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Bg; ForeColor = Theme.Text; Font = UI.F(9.5f, false);

            var side = new Panel { Left = 0, Top = 0, Width = 210, Height = 590, BackColor = Theme.Side };
            side.Controls.Add(new Label { Text = "PC Watch", Font = UI.F(16f, true), Left = 20, Top = 24, Width = 180, Height = 40, ForeColor = Theme.Text });
            for (int i = 0; i < 6; i++)
            {
                stepLbl[i] = new Label { Text = (i + 1) + "   " + Steps[i], Left = 20, Top = 100 + i * 38, Width = 180, Height = 28, Font = UI.F(10f, false), ForeColor = Theme.Muted };
                side.Controls.Add(stepLbl[i]);
            }
            side.Controls.Add(new Label { Text = "Designed by MD", Left = 20, Top = 550, Width = 180, Height = 22, ForeColor = Theme.Muted, Font = UI.F(8.5f, false) });
            Controls.Add(side);
            title = new Label { Left = 236, Top = 20, Width = 570, Height = 40, Font = UI.F(16f, true), ForeColor = Theme.Text };
            Controls.Add(title);
            for (int i = 0; i < 6; i++) { pg[i] = new Panel { Left = 236, Top = 72, Width = 580, Height = 430, BackColor = Theme.Bg, Visible = false }; Controls.Add(pg[i]); }
            bBack = Btn2("Back", 0, 480); bNext = Btn2("Continue", 1, 590); bCancel = Btn2("Cancel", 0, 700);
            bBack.Click += delegate { Go(cur == 4 ? 3 : cur - 1); };
            bNext.Click += delegate { Next(); };
            bCancel.Click += delegate { if (installing) return; if (cur == 5 || MessageBox.Show(this, "Cancel the setup?", "PC Watch Setup", MessageBoxButtons.YesNo) == DialogResult.Yes) Close(); };
            Controls.AddRange(new Control[] { bBack, bNext, bCancel });
            BuildWelcome(); BuildLocation(); BuildDeps(); BuildConfig(); BuildInstall(); BuildFinish();
            Go(0);
        }

        Btn Btn2(string t, int kind, int x) { var b = new Btn(t, kind) { AutoSize = false, Size = new Size(98, 36), Left = x, Top = 534 }; return b; }
        Label L(Control p, string t, int x, int y, int w, int h) { var l = new Label { Text = t, Left = x, Top = y, Width = w, Height = h, ForeColor = Theme.Text, BackColor = Color.Transparent }; p.Controls.Add(l); return l; }
        TextBox T(Control p, int x, int y, int w, bool pw) { var t = new TextBox { Left = x, Top = y, Width = w, UseSystemPasswordChar = pw, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle }; p.Controls.Add(t); return t; }
        void Ui(Action a) { if (IsHandleCreated && !IsDisposed) { try { BeginInvoke(a); } catch { } } }

        // ---- pages ---------------------------------------------------------------
        void BuildWelcome()
        {
            var p = pg[0];
            var t = L(p, "PC Watch lets you monitor and manage this PC remotely through a password-protected web page that is published over an ngrok HTTPS tunnel.\r\n\r\nThis setup will:\r\n   -  reuse or install Python and ngrok (official downloads, signatures checked)\r\n   -  install the PC Watch server\r\n   -  generate PCWatch.exe (the dashboard) on this PC with the Windows C# compiler\r\n   -  create Start Menu shortcuts\r\n\r\nThe dashboard never opens by itself: not after setup, not at Windows startup. You start PCWatch.exe when you want it.\r\n\r\nVersion " + Program.Version, 0, 0, 560, 270);
            t.Font = UI.F(10f, false);
            lExisting = L(p, existing ? "An existing installation was found in " + o.Dir + ".\r\nSetup will repair / update it and keep your settings." : "", 0, 290, 560, 60);
            lExisting.ForeColor = Theme.Warn;
        }

        void BuildLocation()
        {
            var p = pg[1];
            L(p, "PC Watch will be installed in this folder:", 0, 0, 560, 24);
            tDir = T(p, 0, 30, 450, false); tDir.Text = o.Dir; tDir.TextChanged += delegate { UpdateDisk(); };
            var br = new Btn("Browse...", 0) { Left = 460, Top = 27, AutoSize = false, Size = new Size(100, 32) };
            br.Click += delegate { using (var f = new FolderBrowserDialog()) if (f.ShowDialog(this) == DialogResult.OK) tDir.Text = Path.Combine(f.SelectedPath, "PC Watch"); };
            p.Controls.Add(br);
            lDisk = L(p, "", 0, 76, 560, 48);
            L(p, "Required: about 450 MB (Python 100 MB, packages 200 MB, ngrok 30 MB, PC Watch).\r\nOnly PC Watch's own subfolders are created: Server, Tools, Runtime, Config, Logs, Source.", 0, 130, 560, 70).ForeColor = Theme.Muted;
            if (existing) tDir.ReadOnly = true;
            UpdateDisk();
        }
        bool DiskOk()
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(tDir.Text.Trim())); var di = new DriveInfo(root);
                bool ok = di.AvailableFreeSpace > 450L * 1024 * 1024;
                lDisk.Text = "Free space on " + root + ": " + (di.AvailableFreeSpace / 1048576) + " MB" + (ok ? "" : "  -  NOT ENOUGH (450 MB needed)");
                lDisk.ForeColor = ok ? Theme.Good : Theme.Bad; return ok;
            }
            catch { lDisk.Text = "Enter a valid folder path."; lDisk.ForeColor = Theme.Bad; return false; }
        }
        void UpdateDisk() { DiskOk(); }

        void BuildDeps()
        {
            var p = pg[2];
            L(p, "Setup checks what is already on this PC and only installs what is missing.", 0, 0, 560, 24);
            lv = new ListView { Left = 0, Top = 30, Width = 570, Height = 300, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            lv.Columns.Add("Component", 170); lv.Columns.Add("Status", 220); lv.Columns.Add("Action", 176);
            p.Controls.Add(lv);
            var rs = new Btn("Scan again", 0) { Left = 0, Top = 340, AutoSize = false, Size = new Size(110, 32) }; rs.Click += delegate { Scan(); }; p.Controls.Add(rs);
            lHint = L(p, "", 120, 342, 450, 40);
        }

        void Scan()
        {
            Paths.Init(o.Dir); lv.Items.Clear(); scanOk = false; bNext.Enabled = false; lHint.Text = "Scanning...";
            var th = new Thread(delegate()
            {
                bool bad = false;
                Action<string, string, string, bool> add = delegate(string c, string s, string a, bool block)
                {
                    if (block) bad = true;
                    Ui(delegate { var it = new ListViewItem(new[] { c, s, a }); if (block) it.ForeColor = Theme.Bad; lv.Items.Add(it); });
                };
                int rel = Deps.NetFxRelease();
                add(".NET Framework 4.7.2+", rel >= 461808 ? "Installed" : "MISSING", rel >= 461808 ? "none" : "Install .NET 4.8", rel < 461808);
                string csc = Deps.Csc(); add("C# compiler (csc.exe)", csc != null ? "Found" : "MISSING", csc != null ? "none" : "Repair .NET", csc == null);
                string origin = ""; bool srcOk = true; try { SourceProvider.Read(out origin); } catch (Exception) { srcOk = false; }
                add("PCWatch.cs source", srcOk ? "Available" : "MISSING", srcOk ? "none" : "Rebuild setup.exe", !srcOk);
                string pv; string py = Deps.FindPython(out pv);
                add("Python 3.9+", py != null ? "Found " + pv : "Not found", py != null ? "Reuse (no reinstall)" : "Download " + Deps.PythonVersion, false);
                add("Python packages", "psutil, mss, Pillow, opencv", "Install into Server\\lib", false);
                string nv; string ng = Deps.FindNgrok(out nv);
                add("ngrok agent v3", ng != null ? "Found " + nv : "Not found", ng != null ? "Reuse (no reinstall)" : "Download from ngrok.com", false);
                if (py == null || ng == null) { bool on = Deps.Online(); add("Internet access", on ? "Reachable" : "UNREACHABLE", on ? "none" : "Connect to the internet", !on); }
                else add("Internet access", "Not needed", "none", false);
                add("Networking", "Loopback only + ngrok HTTPS", "No firewall rule needed", false);
                Ui(delegate { scanOk = !bad; bNext.Enabled = scanOk; lHint.Text = bad ? "Fix the items in red, then press 'Scan again'." : "All set. Press Next."; lHint.ForeColor = bad ? Theme.Bad : Theme.Good; });
            });
            th.IsBackground = true; th.Start();
        }

        void BuildConfig()
        {
            var p = pg[3]; int y = 0; int step = 34;
            L(p, "Server port", 0, y + 4, 200, 24); nPort = new NumericUpDown { Left = 210, Top = y, Width = 120, Minimum = 1024, Maximum = 65535, Value = Math.Max(1024, Math.Min(65535, o.Port)), BackColor = Theme.Input, ForeColor = Theme.Text }; p.Controls.Add(nPort); y += step;
            L(p, "Remote username", 0, y + 4, 200, 24); tUser = T(p, 210, y, 340, false); tUser.Text = o.User; y += step;
            L(p, existing && hasAuth ? "New password (blank = keep)" : "Remote password", 0, y + 4, 200, 24); tPw = T(p, 210, y, 340, true); y += step;
            L(p, "Confirm password", 0, y + 4, 200, 24); tPw2 = T(p, 210, y, 340, true); y += step;
            L(p, "ngrok authtoken", 0, y + 4, 200, 24); tTok = T(p, 210, y, 340, true); y += step;
            L(p, "ngrok domain (optional)", 0, y + 4, 200, 24); tDom = T(p, 210, y, 340, false); tDom.Text = o.Domain; y += step;
            L(p, "Windows startup", 0, y + 4, 200, 24);
            cStart = new ComboBox { Left = 210, Top = y, Width = 340, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Input, ForeColor = Theme.Text };
            cStart.Items.AddRange(new object[] { "Disabled", "Start server at Windows startup", "Start server and tunnel at Windows startup" }); cStart.SelectedIndex = Math.Max(0, Math.Min(2, o.StartupMode)); p.Controls.Add(cStart); y += step;
            L(p, "Log folder (blank = default)", 0, y + 4, 200, 24); tLog = T(p, 210, y, 340, false); tLog.Text = o.LogDir; y += step;
            kDesk = new CheckBox { Text = "Create a desktop shortcut", Left = 0, Top = y, Width = 400, ForeColor = Theme.Text, Checked = false }; p.Controls.Add(kDesk); y += 30;
            var h = L(p, "Password: 12+ characters with lowercase, uppercase, a digit and a symbol. The authtoken is stored encrypted (Windows DPAPI). Get it at dashboard.ngrok.com > Your Authtoken. Windows startup only starts the hidden background component, never the dashboard.", 0, y, 570, 70);
            h.ForeColor = Theme.Muted;
        }

        bool ValidateConfig()
        {
            int port = (int)nPort.Value;
            if (PortTools.InUse(port) && !(existing && port == Settings.GetInt("server_port", 8765))) { Warn("Port " + port + " is already in use by another program. Choose another port."); return false; }
            string u = tUser.Text.Trim(), pe = Policy.CheckUser(u);
            if (pe != null) { Warn(pe); return false; }
            bool needPw = !(existing && hasAuth) || tPw.Text.Length > 0 || u != o.User;
            if (needPw)
            {
                pe = Policy.CheckPassword(tPw.Text, u); if (pe == null && tPw.Text != tPw2.Text) pe = "The two passwords do not match.";
                if (pe != null) { Warn(pe); return false; }
            }
            string tok = tTok.Text.Trim();
            if (tok.Length > 0 && (tok.Length < 20 || !Regex.IsMatch(tok, @"^[A-Za-z0-9_]+$"))) { Warn("That does not look like an ngrok authtoken."); return false; }
            if (tok.Length == 0 && !(existing && File.Exists(Paths.TokenFile)))
                if (MessageBox.Show(this, "No ngrok authtoken entered. Without it the tunnel cannot start (you can add it later in the dashboard).\r\n\r\nContinue without a token?", "PC Watch Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            string dom = tDom.Text.Trim();
            if (dom.Length > 0 && !Regex.IsMatch(dom, @"^[A-Za-z0-9][A-Za-z0-9.-]*\.[A-Za-z]{2,}$")) { Warn("The ngrok domain must look like name.ngrok-free.app."); return false; }
            string ld = tLog.Text.Trim();
            if (ld.Length > 0) { try { if (!Path.IsPathRooted(ld)) throw new ArgumentException("use a full path such as D:\\Logs"); Path.GetFullPath(ld); } catch (Exception ex) { Warn("Invalid log folder: " + ex.Message); return false; } }
            o.Port = port; o.User = u; o.Password = needPw ? tPw.Text : ""; o.Token = tok; o.Domain = dom; o.StartupMode = cStart.SelectedIndex; o.LogDir = ld; o.DesktopShortcut = kDesk.Checked;
            return true;
        }
        void Warn(string m) { MessageBox.Show(this, m, "PC Watch Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        void BuildInstall()
        {
            var p = pg[4];
            bar = new ProgressBar { Left = 0, Top = 0, Width = 570, Height = 22, Maximum = 100 }; p.Controls.Add(bar);
            lStatus = L(p, "", 0, 30, 570, 26);
            rt = new RichTextBox { Left = 0, Top = 62, Width = 570, Height = 360, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = new Font("Consolas", 8.75f), WordWrap = true };
            p.Controls.Add(rt);
        }
        void AppendLog(string lvl, string m)
        {
            rt.SelectionStart = rt.TextLength; rt.SelectionLength = 0;
            rt.SelectionColor = lvl == "ERROR" ? Theme.Bad : lvl == "WARNING" ? Theme.Warn : Theme.Text;
            rt.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + m + "\n"); rt.ScrollToCaret();
        }

        void StartInstall()
        {
            installing = true; bBack.Enabled = false; bNext.Enabled = false; bCancel.Enabled = false; rt.Clear(); bar.Value = 0; lStatus.ForeColor = Theme.Text;
            var th = new Thread(delegate()
            {
                string err = null, help = null; InstallEngine eng = null;
                try
                {
                    eng = new InstallEngine(o, delegate(int pct, string m) { Ui(delegate { bar.Value = Math.Max(0, Math.Min(100, pct)); lStatus.Text = m; }); },
                        delegate(string lvl, string m) { Ui(delegate { AppendLog(lvl, m); }); });
                    eng.Run();
                }
                catch (InstallException ie) { err = ie.Message; help = ie.Help; Log.Error("Installation", "FAILED: " + ie.Message); }
                catch (Exception ex) { err = "Unexpected error: " + ex.Message; help = "See the Installation log for details."; Log.Error("Installation", "FAILED: " + ex); }
                Ui(delegate { Finished(err, help, eng); });
            });
            th.IsBackground = true; th.Start();
        }

        void Finished(string err, string help, InstallEngine eng)
        {
            installing = false; engine = eng; bCancel.Enabled = true;
            if (err == null) { Go(5); return; }
            lStatus.Text = "Installation FAILED - nothing was reported as installed."; lStatus.ForeColor = Theme.Bad;
            AppendLog("ERROR", "FAILED: " + err); AppendLog("ERROR", "What to do: " + help); AppendLog("INFO", "Full log: " + Path.Combine(Log.Dir, "install.log"));
            bBack.Enabled = true; bNext.Enabled = true; bNext.Text = "Retry"; bCancel.Text = "Close";
            MessageBox.Show(this, err + "\r\n\r\nWhat to do: " + help, "Installation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        void BuildFinish()
        {
            var p = pg[5];
            lDone = L(p, "", 0, 0, 570, 34); lDone.Font = UI.F(13f, true);
            rtDone = new RichTextBox { Left = 0, Top = 44, Width = 570, Height = 260, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = UI.F(9.5f, false) };
            p.Controls.Add(rtDone);
            kStartNow = new CheckBox { Text = "Start the PC Watch background component now (server + tunnel, no window)", Left = 0, Top = 320, Width = 570, ForeColor = Theme.Text };
            kLaunch = new CheckBox { Text = "Open the PC Watch dashboard now (otherwise start it later from the Start menu)", Left = 0, Top = 352, Width = 570, ForeColor = Theme.Text };
            p.Controls.Add(kStartNow); p.Controls.Add(kLaunch);
        }

        // ---- navigation ------------------------------------------------------------
        void Go(int i)
        {
            cur = i;
            for (int k = 0; k < 6; k++) { pg[k].Visible = k == i; stepLbl[k].ForeColor = k == i ? Theme.Accent : (k < i ? Theme.Text : Theme.Muted); stepLbl[k].Font = UI.F(10f, k == i); }
            title.Text = Steps[i]; bNext.Enabled = true; bBack.Enabled = i >= 1 && i <= 3; bCancel.Visible = true; bCancel.Text = "Cancel"; bBack.Visible = i != 5;
            string[] nt = { "Continue", "Next", "Next", "Install", "Retry", "Finish" }; bNext.Text = nt[i];
            if (i == 2) Scan();
            if (i == 4) StartInstall();
            if (i == 5) ShowFinish();
        }

        void Next()
        {
            switch (cur)
            {
                case 0: Go(1); break;
                case 1:
                    string d = tDir.Text.Trim();
                    try { if (!Path.IsPathRooted(d) || Path.GetPathRoot(d).Equals(d, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a folder, not a drive root."); d = Path.GetFullPath(d); }
                    catch (Exception ex) { Warn("Invalid install folder: " + ex.Message); return; }
                    if (!DiskOk()) { Warn("There is not enough free disk space."); return; }
                    if (!existing && Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length > 0 && !File.Exists(Path.Combine(d, "PCWatch.exe")))
                        if (MessageBox.Show(this, "The folder is not empty. PC Watch will only add its own subfolders and files; unrelated files are never touched.\r\n\r\nUse this folder?", "PC Watch Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    o.Dir = d; Go(2); break;
                case 2: Go(3); break;
                case 3: if (ValidateConfig()) Go(4); break;
                case 4: Go(4); break;   // Retry
                case 5: FinishAndClose(); break;
            }
        }

        void ShowFinish()
        {
            bool ok = engine != null;
            lDone.Text = "PC Watch was installed successfully."; lDone.ForeColor = Theme.Good; bNext.Text = "Finish";
            var sb = new StringBuilder();
            sb.AppendLine("Installed in: " + Paths.InstallDir);
            sb.AppendLine("PCWatch.exe was generated from PCWatch.cs and passed its self-test.");
            sb.AppendLine("Python " + engine.PythonVer + " : " + engine.PythonPath);
            sb.AppendLine("ngrok " + engine.NgrokVer + " : " + engine.NgrokPath);
            sb.AppendLine("Server port: " + o.Port + "   Remote user: " + o.User);
            sb.AppendLine("Start Menu: PC Watch (dashboard) and Uninstall PC Watch." + (o.DesktopShortcut ? " Desktop shortcut created." : ""));
            sb.AppendLine("Windows startup: " + (o.StartupMode == 0 ? "disabled" : o.StartupMode == 1 ? "server only (hidden, no dashboard)" : "server + tunnel (hidden, no dashboard)"));
            if (engine.Warnings.Count > 0) { sb.AppendLine(); sb.AppendLine("Warnings (still need your attention):"); foreach (string w in engine.Warnings) sb.AppendLine("  - " + w); }
            sb.AppendLine(); sb.AppendLine("The dashboard does NOT open automatically. Start it from the Start menu when you want it.");
            rtDone.Text = sb.ToString();
        }

        void FinishAndClose()
        {
            try
            {
                if (kStartNow.Checked)
                {
                    Desired.Set("server", "1", "tunnel", File.Exists(Paths.TokenFile) ? "1" : "0", "restart_server", DateTime.UtcNow.Ticks.ToString());
                    Process.Start("explorer.exe", "\"" + Path.Combine(Paths.InstallDir, "PC Watch Background.lnk") + "\"");   // via Explorer so it runs without admin rights
                }
                if (kLaunch.Checked) Process.Start("explorer.exe", "\"" + Path.Combine(Shortcut.StartMenuDir, "PC Watch.lnk") + "\"");
            }
            catch (Exception ex) { Warn("Could not start PC Watch: " + ex.Message); }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { if (installing) e.Cancel = true; base.OnFormClosing(e); }
    }

    internal static class SetupApp
    {
        public static int Run(string[] args, bool repair)
        {
            if (!SysInfo.IsAdmin()) { Elevate.Relaunch(args); return 0; }
            string dir = repair && args.Length > 1 ? args[1] : null;
            if (repair && dir == null && Maint.InsideInstallDir()) dir = Paths.InstallDir;
            if (repair && Maint.InsideInstallDir())
            {
                // never rebuild PCWatch.exe while it is the running image: continue from a temp copy
                string tmp = Path.Combine(Path.GetTempPath(), "PCWatch-repair-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(Application.ExecutablePath, tmp, true);
                Process.Start(new ProcessStartInfo(tmp, "--repair \"" + dir + "\"") { UseShellExecute = true });
                return 0;
            }
            Application.Run(new SetupWizard(repair, dir));
            return 0;
        }
    }

    // ========================================================================
    //  UNINSTALLER
    // ========================================================================
    internal static class Uninstaller
    {
        public static int Run(string[] args)
        {
            if (args.Length > 1) Paths.Init(args[1]);
            if (Maint.InsideInstallDir())
            {
                string tmp = Path.Combine(Path.GetTempPath(), "PCWatch-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(Application.ExecutablePath, tmp, true);
                try { Process.Start(new ProcessStartInfo(tmp, "--uninstall \"" + Paths.InstallDir + "\"") { UseShellExecute = true, Verb = "runas" }); } catch (System.ComponentModel.Win32Exception) { }
                return 0;
            }
            if (!SysInfo.IsAdmin()) { Elevate.Relaunch(new[] { "--uninstall", Paths.InstallDir }); return 0; }
            Application.Run(new UninstallForm());
            return 0;
        }
    }

    internal sealed class UninstallForm : Form
    {
        readonly RadioButton rKeep, rDel; readonly RichTextBox rt; readonly Btn bGo, bClose; bool busy;
        public UninstallForm()
        {
            Text = "Uninstall PC Watch"; Icon = IconFactory.MakeIcon(Theme.Accent); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(620, 470); StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = UI.F(9.5f, false);
            Controls.Add(new Label { Text = "Uninstall PC Watch", Left = 24, Top = 18, Width = 560, Height = 34, Font = UI.F(15f, true) });
            Controls.Add(new Label { Text = "This stops the PC Watch server and tunnel, and removes the program, shortcuts and startup entry from:\r\n" + Paths.InstallDir, Left = 24, Top = 60, Width = 570, Height = 50, ForeColor = Theme.Muted });
            rKeep = new RadioButton { Text = "Keep my settings, credentials and logs (recommended)", Left = 24, Top = 118, Width = 560, Checked = true, ForeColor = Theme.Text };
            rDel = new RadioButton { Text = "Also delete my settings, credentials and logs", Left = 24, Top = 146, Width = 560, ForeColor = Theme.Text };
            rt = new RichTextBox { Left = 24, Top = 184, Width = 572, Height = 220, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = new Font("Consolas", 8.75f) };
            bGo = new Btn("Uninstall", 2) { Left = 392, Top = 420, AutoSize = false, Size = new Size(100, 36) }; bClose = new Btn("Cancel", 0) { Left = 500, Top = 420, AutoSize = false, Size = new Size(96, 36) };
            bGo.Click += delegate { Go(); }; bClose.Click += delegate { if (!busy) Close(); };
            Controls.AddRange(new Control[] { rKeep, rDel, rt, bGo, bClose });
        }
        void Say(string m, bool err)
        {
            Action a = delegate { rt.SelectionStart = rt.TextLength; rt.SelectionColor = err ? Theme.Bad : Theme.Text; rt.AppendText(m + "\n"); rt.ScrollToCaret(); };
            if (InvokeRequired) { try { BeginInvoke(a); } catch { } } else a();
            Log.Write(err ? "ERROR" : "INFO", "Installation", "[uninstall] " + m);
        }
        void Go()
        {
            busy = true; bGo.Enabled = false; bClose.Enabled = false; rKeep.Enabled = false; rDel.Enabled = false; bool del = rDel.Checked;
            var th = new Thread(delegate()
            {
                int problems = 0;
                try { problems = Remove(del); } catch (Exception ex) { Say("Uninstall failed: " + ex.Message, true); problems++; }
                Invoke(new Action(delegate
                {
                    busy = false; bClose.Enabled = true; bClose.Text = "Close";
                    Say(problems == 0 ? "PC Watch was removed." : "Uninstall finished with " + problems + " problem(s) - some items could not be removed (see above).", problems > 0);
                }));
            });
            th.IsBackground = true; th.Start();
        }

        bool DelDir(string d)
        {
            for (int i = 0; i < 5; i++)
            {
                try { if (!Directory.Exists(d)) return true; Directory.Delete(d, true); return true; }
                catch (Exception ex) { if (i == 4) { Say("Could not remove " + d + ": " + ex.Message, true); return false; } Thread.Sleep(600); }
            }
            return false;
        }
        bool DelFile(string f)
        {
            for (int i = 0; i < 5; i++)
            {
                try { if (File.Exists(f)) File.Delete(f); return true; }
                catch (Exception ex) { if (i == 4) { Say("Could not remove " + f + ": " + ex.Message, true); return false; } Thread.Sleep(600); }
            }
            return false;
        }

        int Remove(bool deleteData)
        {
            int problems = 0; string dir = Paths.InstallDir, customLog = Settings.Get("log_dir");
            Say("Stopping PC Watch server, tunnel and background component...", false);
            Maint.StopEverything(dir, delegate(string m) { Say(m, false); });
            try { StartupReg.Apply(0); Say("Removed the Windows startup entry.", false); } catch (Exception ex) { Say("Could not remove the startup entry: " + ex.Message, true); problems++; }
            try { if (Directory.Exists(Shortcut.StartMenuDir)) Directory.Delete(Shortcut.StartMenuDir, true); DelFile(Shortcut.DesktopLnk); Say("Removed shortcuts.", false); } catch (Exception ex) { Say("Could not remove shortcuts: " + ex.Message, true); problems++; }
            string pyInst = Path.Combine(Paths.RuntimeDir, "python-installer.exe");
            if (File.Exists(pyInst) && Directory.Exists(Path.Combine(Paths.RuntimeDir, "Python")))
            {
                Say("Uninstalling the private Python copy that setup installed...", false);
                string o1; int c = ProcTools.Run(pyInst, "/uninstall /quiet", 300000, out o1);
                if (c != 0) { Say("The Python uninstaller returned code " + c + "; removing its folder anyway.", true); problems++; }
            }
            foreach (string sub in new[] { "Server", "Tools", "Runtime", "Source" }) if (!DelDir(Path.Combine(dir, sub))) problems++;
            if (!DelDir(Paths.RunDir)) problems++;
            foreach (string f in new[] { "PCWatch.new.exe", "PC Watch Background.lnk" }) if (!DelFile(Path.Combine(dir, f))) problems++;
            if (deleteData)
            {
                Say("Deleting settings, credentials and logs...", false);
                if (!DelDir(Paths.ConfigDir)) problems++;
                if (!DelDir(Paths.DefaultLogDir)) problems++;
                if (!string.IsNullOrWhiteSpace(customLog) && Directory.Exists(customLog))
                {
                    foreach (string n in Log.AllFiles) foreach (string f in Directory.GetFiles(customLog, n + "*")) DelFile(f);   // only PC Watch's own log files
                    try { Directory.Delete(customLog); } catch { }
                }
            }
            else Say("Kept Config and Logs in " + dir + ".", false);
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PCWatch", false); } catch (Exception ex) { Say("Could not remove the Programs & Features entry: " + ex.Message, true); problems++; }
            if (!DelFile(Path.Combine(dir, "PCWatch.exe"))) problems++;
            try { Directory.Delete(dir); Say("Removed the empty install folder.", false); } catch { }   // non-recursive: only succeeds when empty, unrelated files are never deleted
            try   // remove this temporary uninstaller copy after it exits
            {
                string me = Application.ExecutablePath;
                if (me.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 4 > nul & del /f /q \"" + me + "\"") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch { }
            return problems;
        }
    }
}
