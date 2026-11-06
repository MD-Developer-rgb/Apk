// =====================================================================
//  AppSwitcher.cs  -  Windows background app switcher & automation tool
//  Single-file Windows Forms app for Windows 10 / 11  (.NET 6, 7, 8+)
//
//  BUILD (see the accompanying instructions):
//    dotnet new winforms -n AppSwitcher
//    cd AppSwitcher
//    del Program.cs Form1.cs Form1.Designer.cs   (template files)
//    copy this AppSwitcher.cs into the folder
//    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
//  Result: out\AppSwitcher.exe
// =====================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AppSwitcherTool
{
    // ------------------------------------------------------------------
    //  Win32 interop
    // ------------------------------------------------------------------
    internal static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const uint WM_CLOSE = 0x0010;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);
    }

    // ------------------------------------------------------------------
    //  Paths + logging
    // ------------------------------------------------------------------
    internal static class AppPaths
    {
        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppSwitcher");
            }
        }
        public static string ConfigFile { get { return Path.Combine(Dir, "config.json"); } }
        public static string LogFile { get { return Path.Combine(Dir, "log.txt"); } }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.Dir);
                    var fi = new FileInfo(AppPaths.LogFile);
                    if (fi.Exists && fi.Length > 1000000) fi.Delete();
                    File.AppendAllText(AppPaths.LogFile,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                }
            }
            catch { /* logging must never throw */ }
        }

        public static void Write(string context, Exception ex)
        {
            Write(context + ": " + ex.GetType().Name + " - " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    //  Configuration model + JSON storage
    // ------------------------------------------------------------------
    internal class AppEntry
    {
        public string FilePath { get; set; } = "";
        // Optional override: the process name to look for (without ".exe").
        public string ProcessName { get; set; } = "";

        public string EffectiveProcessName()
        {
            string n = string.IsNullOrWhiteSpace(ProcessName)
                ? Path.GetFileNameWithoutExtension(FilePath ?? "")
                : ProcessName.Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            return n;
        }

        public override string ToString()
        {
            return EffectiveProcessName() + "   \u2014   " + FilePath;
        }
    }

    internal class AppConfig
    {
        public uint HotkeyModifiers { get; set; } = Native.MOD_CONTROL | Native.MOD_ALT;
        public int HotkeyKey { get; set; } = (int)Keys.F12;
        public bool Enabled { get; set; } = true;
        public bool ForceTerminate { get; set; } = false;
        public bool StartWithWindows { get; set; } = false;

        // --- Hidden mode (hide tray / taskbar icons) ---
        public uint HideHotkeyModifiers { get; set; } = Native.MOD_CONTROL | Native.MOD_ALT;
        public int HideHotkeyKey { get; set; } = (int)Keys.F11;
        public bool HideTrayIcon { get; set; } = true;
        public bool HideTaskbarIcon { get; set; } = true;
        public bool HiddenModeActive { get; set; } = false;

        public List<AppEntry> CloseApps { get; set; } = new List<AppEntry>();
        public List<AppEntry> OpenApps { get; set; } = new List<AppEntry>();
        public List<string> Urls { get; set; } = new List<string>();

        public AppConfig Clone()
        {
            return ConfigStore.Deserialize(ConfigStore.Serialize(this)) ?? new AppConfig();
        }
    }

    internal static class ConfigStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static string Serialize(AppConfig cfg) { return JsonSerializer.Serialize(cfg, Options); }

        public static AppConfig Deserialize(string json)
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (cfg == null) return null;
            if (cfg.CloseApps == null) cfg.CloseApps = new List<AppEntry>();
            if (cfg.OpenApps == null) cfg.OpenApps = new List<AppEntry>();
            if (cfg.Urls == null) cfg.Urls = new List<string>();
            cfg.CloseApps = cfg.CloseApps.Where(a => a != null && !string.IsNullOrWhiteSpace(a.FilePath)).ToList();
            cfg.OpenApps = cfg.OpenApps.Where(a => a != null && !string.IsNullOrWhiteSpace(a.FilePath)).ToList();
            cfg.Urls = cfg.Urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            return cfg;
        }

        public static bool DefaultExists() { return File.Exists(AppPaths.ConfigFile); }

        public static AppConfig Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Deserialize(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Log.Write("Load config failed (" + path + ")", ex);
                return null;
            }
        }

        public static bool Save(AppConfig cfg, string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, Serialize(cfg), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Save config failed (" + path + ")", ex);
                return false;
            }
        }
    }

    // ------------------------------------------------------------------
    //  Start with Windows (HKCU Run key - no admin rights required)
    // ------------------------------------------------------------------
    internal static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AppSwitcher";

        public static void Set(bool enable)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable)
                {
                    string exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                        key.SetValue(ValueName, "\"" + exe + "\" --background");
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    //  Process detection / closing / launching
    // ------------------------------------------------------------------
    internal static class ProcessHelper
    {
        private static string NormalizePath(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\'); } catch { return p; }
        }

        internal static string GetProcessPath(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { Native.CloseHandle(h); }
        }

        // Finds ALL running processes belonging to the app (matches by process
        // name first, then by full executable path, so renamed/launcher
        // processes and multi-process apps like Chrome/Discord are covered).
        public static List<Process> Find(AppEntry app)
        {
            var result = new List<Process>();
            string name = app.EffectiveProcessName();
            bool havePath = !string.IsNullOrWhiteSpace(app.FilePath);
            string fullPath = havePath ? NormalizePath(app.FilePath) : null;
            int self = Environment.ProcessId;

            foreach (var p in Process.GetProcesses())
            {
                bool match = false;
                try
                {
                    if (p.Id != self)
                    {
                        if (!string.IsNullOrEmpty(name) &&
                            string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                        {
                            match = true;
                        }
                        else if (havePath)
                        {
                            string pp = GetProcessPath(p.Id);
                            if (pp != null && string.Equals(NormalizePath(pp), fullPath, StringComparison.OrdinalIgnoreCase))
                                match = true;
                        }
                    }
                }
                catch { /* process vanished or access denied */ }

                if (match) result.Add(p); else p.Dispose();
            }
            return result;
        }

        private static bool Exited(Process p)
        {
            try { p.Refresh(); return p.HasExited; }
            catch (InvalidOperationException) { return true; }
            catch { return false; }
        }

        public static async Task CloseAsync(IEnumerable<AppEntry> apps, bool force)
        {
            var procs = new Dictionary<int, Process>();
            try
            {
                foreach (var app in apps)
                {
                    foreach (var p in Find(app))
                    {
                        if (!procs.ContainsKey(p.Id)) procs[p.Id] = p; else p.Dispose();
                    }
                }
                if (procs.Count == 0) return; // everything already closed: silently skip

                // ---- Graceful: ask every visible top-level window to close ----
                var ids = new HashSet<uint>(procs.Keys.Select(i => (uint)i));
                Native.EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        uint pid;
                        Native.GetWindowThreadProcessId(hWnd, out pid);
                        if (ids.Contains(pid) && Native.IsWindowVisible(hWnd))
                            Native.PostMessage(hWnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);

                foreach (var p in procs.Values)
                {
                    try { if (!p.HasExited && p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); }
                    catch { }
                }

                if (await WaitForExitAsync(procs.Values, 6000)) return;

                // ---- Still running ----
                var remaining = procs.Values.Where(p => !Exited(p)).ToList();
                if (!force)
                {
                    Log.Write(remaining.Count + " process(es) did not close gracefully; force termination is disabled.");
                    return;
                }

                foreach (var p in remaining)
                {
                    try { p.Kill(true); }
                    catch (Exception ex) { Log.Write("Force-terminate failed (pid " + p.Id + ")", ex); }
                }
                await WaitForExitAsync(remaining, 3000);
            }
            finally
            {
                foreach (var p in procs.Values) { try { p.Dispose(); } catch { } }
            }
        }

        private static async Task<bool> WaitForExitAsync(IEnumerable<Process> procs, int timeoutMs)
        {
            var list = procs.ToList();
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (list.All(Exited)) return true;
                await Task.Delay(200);
            }
            return list.All(Exited);
        }

        public static void OpenApp(AppEntry app)
        {
            if (string.IsNullOrWhiteSpace(app.FilePath) || !File.Exists(app.FilePath))
            {
                Log.Write("Open skipped - file not found: " + app.FilePath);
                return;
            }

            var running = Find(app);
            bool isRunning = running.Count > 0;
            foreach (var p in running) p.Dispose();
            if (isRunning) return; // already running: never launch a duplicate

            var psi = new ProcessStartInfo(app.FilePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(app.FilePath) ?? ""
            };
            Process started = Process.Start(psi);
            if (started != null) started.Dispose();
        }
    }

    // ------------------------------------------------------------------
    //  Action sequence (close -> open apps -> open URLs), run once per trigger
    // ------------------------------------------------------------------
    internal static class ActionRunner
    {
        private static int _busy;

        public static bool TryNormalizeUrl(string input, out string url)
        {
            url = null;
            if (string.IsNullOrWhiteSpace(input)) return false;
            input = input.Trim();
            if (!input.Contains("://")) input = "https://" + input;

            Uri u;
            if (!Uri.TryCreate(input, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
            if (string.IsNullOrEmpty(u.Host)) return false;
            if (!u.Host.Contains('.') && !string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                return false;

            url = u.AbsoluteUri;
            return true;
        }

        public static async Task RunAsync(AppConfig cfg)
        {
            // Guard: ignore re-entry while a sequence is still running.
            if (Interlocked.Exchange(ref _busy, 1) == 1)
            {
                Log.Write("Actions already running - trigger ignored.");
                return;
            }

            try
            {
                Log.Write("Running shortcut actions.");

                try { await ProcessHelper.CloseAsync(cfg.CloseApps, cfg.ForceTerminate); }
                catch (Exception ex) { Log.Write("Close step failed", ex); }

                foreach (var app in cfg.OpenApps)
                {
                    try { ProcessHelper.OpenApp(app); }
                    catch (Exception ex) { Log.Write("Open failed (" + app.FilePath + ")", ex); }
                    await Task.Delay(150);
                }

                foreach (var raw in cfg.Urls)
                {
                    string url;
                    if (!TryNormalizeUrl(raw, out url))
                    {
                        Log.Write("Skipped invalid URL: " + raw);
                        continue;
                    }
                    try
                    {
                        Process p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        if (p != null) p.Dispose();
                    }
                    catch (Exception ex) { Log.Write("URL launch failed (" + url + ")", ex); }
                    await Task.Delay(250);
                }
            }
            catch (Exception ex) { Log.Write("RunAsync failed", ex); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }
    }

    // ------------------------------------------------------------------
    //  Hidden message-only window that receives WM_HOTKEY
    // ------------------------------------------------------------------
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public const int HotkeyId = 0xA11C;
        public const int HideHotkeyId = 0xA11D;
        public event Action<int> Pressed;
        private readonly HashSet<int> _registered = new HashSet<int>();

        public HotkeyWindow()
        {
            var cp = new CreateParams();
            cp.Parent = new IntPtr(-3); // HWND_MESSAGE
            CreateHandle(cp);
        }

        public bool Register(uint modifiers, int vk, out string error)
        {
            return Register(HotkeyId, modifiers, vk, out error);
        }

        public bool Register(int id, uint modifiers, int vk, out string error)
        {
            Unregister(id); // never register the same id twice
            error = null;
            if (vk == 0 || modifiers == 0)
            {
                error = "No shortcut configured (a modifier key plus a key is required).";
                return false;
            }
            // MOD_NOREPEAT: holding the keys down does not retrigger the actions.
            if (Native.RegisterHotKey(Handle, id, modifiers | Native.MOD_NOREPEAT, (uint)vk))
            {
                _registered.Add(id);
                return true;
            }
            int code = Marshal.GetLastWin32Error();
            error = code == 1409
                ? "That shortcut is already used by another program. Choose a different one."
                : "Could not register the shortcut (Win32 error " + code + ").";
            return false;
        }

        public void Unregister()
        {
            Unregister(HotkeyId);
        }

        public void Unregister(int id)
        {
            if (_registered.Remove(id) && Handle != IntPtr.Zero)
            {
                Native.UnregisterHotKey(Handle, id);
            }
        }

        public void UnregisterAll()
        {
            foreach (int id in _registered.ToList()) Unregister(id);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HotkeyId || id == HideHotkeyId)
                {
                    var handler = Pressed;
                    if (handler != null) handler(id);
                    return;
                }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterAll();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    // ------------------------------------------------------------------
    //  UI controls
    // ------------------------------------------------------------------
    internal sealed class HotkeyBox : TextBox
    {
        public uint Modifiers { get; private set; }
        public int VirtualKey { get; private set; }

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            Cursor = Cursors.IBeam;
        }

        public void SetHotkey(uint mods, int vk)
        {
            Modifiers = mods;
            VirtualKey = vk;
            Text = Describe(mods, vk);
        }

        public static string Describe(uint mods, int vk)
        {
            if (vk == 0) return "(none)";
            var sb = new StringBuilder();
            if ((mods & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & Native.MOD_WIN) != 0) sb.Append("Win+");
            string name = ((Keys)vk).ToString();
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name.Substring(1);
            sb.Append(name);
            return sb.ToString();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!Focused) return base.ProcessCmdKey(ref msg, keyData);

            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.None || key == Keys.ControlKey || key == Keys.ShiftKey ||
                key == Keys.Menu || key == Keys.LWin || key == Keys.RWin)
                return true; // modifier alone - wait for the real key

            uint mods = 0;
            if ((keyData & Keys.Control) != 0) mods |= Native.MOD_CONTROL;
            if ((keyData & Keys.Alt) != 0) mods |= Native.MOD_ALT;
            if ((keyData & Keys.Shift) != 0) mods |= Native.MOD_SHIFT;
            if ((Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0)
                mods |= Native.MOD_WIN;

            if (mods == 0)
            {
                if (key == Keys.Back || key == Keys.Delete) SetHotkey(0, 0);
                return true; // a shortcut needs at least one modifier
            }

            SetHotkey(mods, (int)key);
            return true;
        }
    }

    internal static class Prompt
    {
        public static string Ask(IWin32Window owner, string title, string label, string initial)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.ClientSize = new Size(420, 124);

                var l = new Label { Text = label, Left = 12, Top = 12, Width = 396, Height = 36 };
                var tb = new TextBox { Left = 12, Top = 52, Width = 396, Text = initial };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 252, Top = 86, Width = 75 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 333, Top = 86, Width = 75 };
                f.Controls.AddRange(new Control[] { l, tb, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }
    }

    internal static class UiHelper
    {
        public static Button MakeButton(string text, EventHandler onClick, bool primary)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                Padding = new Padding(10, 3, 10, 3),
                Margin = new Padding(0, 0, 8, 0),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            if (primary)
            {
                b.BackColor = Color.FromArgb(0, 120, 212);
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderSize = 0;
            }
            else
            {
                b.BackColor = Color.FromArgb(243, 243, 243);
                b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            }
            if (onClick != null) b.Click += onClick;
            return b;
        }
    }

    internal sealed class AppListPanel : GroupBox
    {
        private readonly ListBox _list = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            HorizontalScrollbar = true,
            BorderStyle = BorderStyle.FixedSingle
        };
        private readonly List<AppEntry> _items = new List<AppEntry>();

        public AppListPanel(string title)
        {
            Text = title;
            Padding = new Padding(8, 6, 8, 8);

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 6, 0, 0)
            };
            bar.Controls.Add(UiHelper.MakeButton("Add .exe\u2026", OnAdd, false));
            bar.Controls.Add(UiHelper.MakeButton("Remove", OnRemove, false));
            bar.Controls.Add(UiHelper.MakeButton("Process name\u2026", OnRename, false));

            // Fill control first, edge controls after (WinForms dock order).
            Controls.Add(_list);
            Controls.Add(bar);
            _list.DoubleClick += OnRename;
        }

        public List<AppEntry> GetItems()
        {
            return _items.Select(i => new AppEntry { FilePath = i.FilePath, ProcessName = i.ProcessName }).ToList();
        }

        public void SetItems(IEnumerable<AppEntry> items)
        {
            _items.Clear();
            foreach (var i in items)
                _items.Add(new AppEntry { FilePath = i.FilePath, ProcessName = i.ProcessName });
            Refill();
        }

        // Adds entries that are not already listed; returns how many were added.
        public int AddEntries(IEnumerable<AppEntry> entries)
        {
            int added = 0;
            foreach (var a in entries)
            {
                if (string.IsNullOrWhiteSpace(a.FilePath)) continue;
                if (_items.Any(i => string.Equals(i.FilePath, a.FilePath, StringComparison.OrdinalIgnoreCase))) continue;
                _items.Add(new AppEntry
                {
                    FilePath = a.FilePath,
                    ProcessName = string.IsNullOrWhiteSpace(a.ProcessName)
                        ? Path.GetFileNameWithoutExtension(a.FilePath)
                        : a.ProcessName
                });
                added++;
            }
            if (added > 0) Refill();
            return added;
        }

        public void RemoveByPath(IEnumerable<string> paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            if (_items.RemoveAll(i => set.Contains(i.FilePath)) > 0) Refill();
        }

        private void Refill()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var i in _items) _list.Items.Add(i.ToString());
            _list.EndUpdate();
        }

        private void OnAdd(object s, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select applications";
                dlg.Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*";
                dlg.Multiselect = true;
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

                AddEntries(dlg.FileNames.Select(f => new AppEntry
                {
                    FilePath = f,
                    ProcessName = Path.GetFileNameWithoutExtension(f)
                }));
            }
        }

        private void OnRemove(object s, EventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx < 0 || idx >= _items.Count) return;
            _items.RemoveAt(idx);
            Refill();
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(idx, _list.Items.Count - 1);
        }

        private void OnRename(object s, EventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx < 0 || idx >= _items.Count) return;
            string v = Prompt.Ask(FindForm(), "Process name",
                "Process name to detect (without .exe). Change this only if the app runs under a different name than its file:",
                _items[idx].EffectiveProcessName());
            if (v == null) return;
            _items[idx].ProcessName = v;
            Refill();
            _list.SelectedIndex = idx;
        }
    }

    internal sealed class UrlListPanel : GroupBox
    {
        private readonly ListBox _list = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            HorizontalScrollbar = true,
            BorderStyle = BorderStyle.FixedSingle
        };
        private readonly TextBox _input = new TextBox { Dock = DockStyle.Top };
        private readonly Label _msg = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Firebrick };

        public UrlListPanel(string title)
        {
            Text = title;
            Padding = new Padding(8, 6, 8, 8);

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Padding = new Padding(0, 6, 0, 0)
            };
            bar.Controls.Add(UiHelper.MakeButton("Add", OnAdd, false));
            bar.Controls.Add(UiHelper.MakeButton("Remove", OnRemove, false));

            Controls.Add(_list);
            Controls.Add(_msg);
            Controls.Add(_input);
            Controls.Add(bar);

            _input.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    OnAdd(null, EventArgs.Empty);
                }
            };
        }

        public List<string> GetItems() { return _list.Items.Cast<string>().ToList(); }

        public void SetItems(IEnumerable<string> urls)
        {
            _list.Items.Clear();
            foreach (var u in urls) _list.Items.Add(u);
        }

        private void OnAdd(object s, EventArgs e)
        {
            string url;
            if (!ActionRunner.TryNormalizeUrl(_input.Text, out url))
            {
                _msg.Text = "Enter a valid http(s) URL, e.g. https://example.com";
                return;
            }
            _msg.Text = "";
            if (!_list.Items.Cast<string>().Any(x => string.Equals(x, url, StringComparison.OrdinalIgnoreCase)))
                _list.Items.Add(url);
            _input.Clear();
        }

        private void OnRemove(object s, EventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx < 0) return;
            _list.Items.RemoveAt(idx);
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(idx, _list.Items.Count - 1);
        }
    }

    // ------------------------------------------------------------------
    //  Settings window
    // ------------------------------------------------------------------
    // Lists the programs running right now so they can be added to the
    // Close list or the Open list with one click.
    internal sealed class RunningProgramsForm : Form
    {
        private sealed class Row
        {
            public string Name = "";
            public string Title = "";
            public string Path = "";
            public int Count;
        }

        private readonly ListView _view = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false
        };
        private readonly TextBox _filter = new TextBox { Width = 200 };
        private readonly CheckBox _onlyWindows = new CheckBox
        {
            Text = "Only programs with a visible window",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(12, 4, 12, 0)
        };
        private readonly Label _info = new Label { AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
        private readonly Func<List<AppEntry>, int> _addToClose;
        private readonly Func<List<AppEntry>, int> _addToOpen;
        private List<Row> _rows = new List<Row>();

        public RunningProgramsForm(Func<List<AppEntry>, int> addToClose, Func<List<AppEntry>, int> addToOpen)
        {
            _addToClose = addToClose;
            _addToOpen = addToOpen;

            Text = "Running programs";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(860, 520);
            MinimumSize = new Size(700, 380);

            _view.Columns.Add("Program", 170);
            _view.Columns.Add("Window title", 250);
            _view.Columns.Add("Instances", 70);
            _view.Columns.Add("Path", 340);

            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(8, 8, 8, 4)
            };
            top.Controls.Add(new Label { Text = "Search:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            top.Controls.Add(_filter);
            top.Controls.Add(_onlyWindows);
            top.Controls.Add(UiHelper.MakeButton("Refresh", (s, e) => RefreshList(), false));

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Padding = new Padding(8)
            };
            bottom.Controls.Add(UiHelper.MakeButton("Add selected to CLOSE list", (s, e) => AddSelected(true), true));
            bottom.Controls.Add(UiHelper.MakeButton("Add selected to OPEN list", (s, e) => AddSelected(false), true));
            bottom.Controls.Add(UiHelper.MakeButton("Done", (s, e) => Close(), false));
            bottom.Controls.Add(_info);

            // Fill control first, edge controls after (WinForms dock order).
            Controls.Add(_view);
            Controls.Add(top);
            Controls.Add(bottom);

            _filter.TextChanged += (s, e) => FillView();
            _onlyWindows.CheckedChanged += (s, e) => FillView();
            Shown += (s, e) => RefreshList();
        }

        private async void RefreshList()
        {
            _info.ForeColor = SystemColors.ControlText;
            _info.Text = "Loading\u2026";
            try
            {
                _rows = await Task.Run(() => Collect());
            }
            catch (Exception ex)
            {
                Log.Write("Listing running programs failed", ex);
                _rows = new List<Row>();
            }
            FillView();
        }

        // One row per executable (all its processes are grouped together).
        private static List<Row> Collect()
        {
            var map = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            int self = Environment.ProcessId;

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self) continue;
                    string path = ProcessHelper.GetProcessPath(p.Id);
                    if (string.IsNullOrEmpty(path)) continue; // protected/system process

                    string title = "";
                    try { title = p.MainWindowTitle ?? ""; } catch { }

                    Row r;
                    if (!map.TryGetValue(path, out r))
                    {
                        r = new Row { Name = p.ProcessName, Title = title, Path = path };
                        map[path] = r;
                    }
                    r.Count++;
                    if (r.Title.Length == 0 && title.Length > 0) r.Title = title;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return map.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void FillView()
        {
            string f = _filter.Text.Trim();
            bool onlyWin = _onlyWindows.Checked;

            _view.BeginUpdate();
            _view.Items.Clear();
            foreach (var r in _rows)
            {
                if (onlyWin && r.Title.Length == 0) continue;
                if (f.Length > 0 &&
                    r.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 &&
                    r.Title.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 &&
                    r.Path.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var item = new ListViewItem(r.Name);
                item.SubItems.Add(r.Title);
                item.SubItems.Add(r.Count.ToString());
                item.SubItems.Add(r.Path);
                item.Tag = r;
                _view.Items.Add(item);
            }
            _view.EndUpdate();
            _info.Text = _view.Items.Count + " program(s) shown";
        }

        private void AddSelected(bool toClose)
        {
            var entries = new List<AppEntry>();
            foreach (ListViewItem item in _view.SelectedItems)
            {
                var r = item.Tag as Row;
                if (r != null) entries.Add(new AppEntry { FilePath = r.Path, ProcessName = r.Name });
            }
            if (entries.Count == 0)
            {
                _info.ForeColor = Color.Firebrick;
                _info.Text = "Select one or more programs first.";
                return;
            }

            int added = toClose ? _addToClose(entries) : _addToOpen(entries);
            _info.ForeColor = Color.SeaGreen;
            _info.Text = added + " added to the " + (toClose ? "CLOSE" : "OPEN") + " list"
                + (added < entries.Count ? " (" + (entries.Count - added) + " already there)" : "") + ".";
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly TrayController _owner;
        private readonly HotkeyBox _hotkey = new HotkeyBox { Width = 240 };
        private readonly Label _hotkeyStatus = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        private readonly CheckBox _enabled = new CheckBox { Text = "Enable automation", AutoSize = true };
        private readonly CheckBox _startup = new CheckBox { Text = "Start automatically with Windows", AutoSize = true };
        private readonly CheckBox _force = new CheckBox
        {
            Text = "Force-terminate apps that don't close gracefully",
            AutoSize = true
        };
        private readonly AppListPanel _close = new AppListPanel("Applications to close");
        private readonly AppListPanel _open = new AppListPanel("Applications to open");
        private readonly UrlListPanel _urls = new UrlListPanel("URLs to open");
        private readonly Label _status = new Label { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };

        private readonly CheckBox _hideTray = new CheckBox { Text = "Hide the tray icon", AutoSize = true };
        private readonly CheckBox _hideTaskbar = new CheckBox { Text = "Hide the taskbar icon", AutoSize = true };
        private readonly CheckBox _hiddenNow = new CheckBox { Text = "Hidden mode is ON right now", AutoSize = true };
        private readonly HotkeyBox _hideHotkey = new HotkeyBox { Width = 240 };
        private readonly Label _hideHotkeyStatus = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };

        public bool AllowClose;

        public SettingsForm(TrayController owner)
        {
            _owner = owner;
            Text = "AppSwitcher \u2014 Settings";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(860, 640);
            MinimumSize = new Size(760, 580);
            Icon = owner.AppIcon;

            BuildUi();
            LoadFromConfig(owner.Config);
            RefreshHotkeyStatus();

            FormClosing += (s, e) =>
            {
                if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    _owner.NotifyHidden();
                }
            };
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // --- Shortcut ---
            var gShortcut = new GroupBox
            {
                Text = "Global shortcut",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 10)
            };
            var shortcutFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            row.Controls.Add(new Label
            {
                Text = "Click the box, then press your shortcut:",
                AutoSize = true,
                Margin = new Padding(0, 6, 8, 0)
            });
            row.Controls.Add(_hotkey);
            row.Controls.Add(UiHelper.MakeButton("Clear", (s, e) => _hotkey.SetHotkey(0, 0), false));
            shortcutFlow.Controls.Add(row);
            shortcutFlow.Controls.Add(_hotkeyStatus);
            gShortcut.Controls.Add(shortcutFlow);

            // --- Options ---
            var gOptions = new GroupBox
            {
                Text = "Options",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 10)
            };
            var optFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };
            _enabled.Margin = new Padding(0, 0, 18, 0);
            _startup.Margin = new Padding(0, 0, 18, 0);
            optFlow.Controls.Add(_enabled);
            optFlow.Controls.Add(_startup);
            optFlow.Controls.Add(_force);
            gOptions.Controls.Add(optFlow);

            // --- Hide tray / taskbar icons ---
            var gHide = new GroupBox
            {
                Text = "Hide tray & taskbar icons",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 10)
            };
            var hideFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            var hideOpts = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };
            _hideTray.Margin = new Padding(0, 0, 18, 0);
            _hideTaskbar.Margin = new Padding(0, 0, 18, 0);
            hideOpts.Controls.Add(_hideTray);
            hideOpts.Controls.Add(_hideTaskbar);
            hideOpts.Controls.Add(_hiddenNow);

            var hideRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            hideRow.Controls.Add(new Label
            {
                Text = "Hide / show shortcut:",
                AutoSize = true,
                Margin = new Padding(0, 6, 8, 0)
            });
            hideRow.Controls.Add(_hideHotkey);
            hideRow.Controls.Add(UiHelper.MakeButton("Clear", (s, e) => _hideHotkey.SetHotkey(0, 0), false));

            hideFlow.Controls.Add(hideOpts);
            hideFlow.Controls.Add(hideRow);
            hideFlow.Controls.Add(_hideHotkeyStatus);
            hideFlow.Controls.Add(new Label
            {
                Text = "Press the shortcut again to bring the icons back. Starting AppSwitcher.exe again also restores them.",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Margin = new Padding(0, 4, 0, 0)
            });
            gHide.Controls.Add(hideFlow);

            // --- Lists ---
            var lists = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            for (int i = 0; i < 3; i++) lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            lists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _close.Dock = DockStyle.Fill; _close.Margin = new Padding(0, 0, 6, 0);
            _open.Dock = DockStyle.Fill; _open.Margin = new Padding(3, 0, 3, 0);
            _urls.Dock = DockStyle.Fill; _urls.Margin = new Padding(6, 0, 0, 0);
            lists.Controls.Add(_close, 0, 0);
            lists.Controls.Add(_open, 1, 0);
            lists.Controls.Add(_urls, 2, 0);

            // --- Buttons ---
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 8, 0, 0)
            };
            buttons.Controls.Add(UiHelper.MakeButton("Save", OnSave, true));
            buttons.Controls.Add(UiHelper.MakeButton("Running programs\u2026", OnRunningPrograms, false));
            buttons.Controls.Add(UiHelper.MakeButton("Load config\u2026", OnLoad, false));
            buttons.Controls.Add(UiHelper.MakeButton("Save config as\u2026", OnSaveAs, false));
            buttons.Controls.Add(UiHelper.MakeButton("Run actions now", (s, e) => _owner.RunNow(), false));
            buttons.Controls.Add(UiHelper.MakeButton("Hide to tray", (s, e) => Close(), false));

            root.Controls.Add(gShortcut, 0, 0);
            root.Controls.Add(gOptions, 0, 1);
            root.Controls.Add(gHide, 0, 2);
            root.Controls.Add(lists, 0, 3);
            root.Controls.Add(buttons, 0, 4);
            root.Controls.Add(_status, 0, 5);
            Controls.Add(root);
        }

        public void LoadFromConfig(AppConfig cfg)
        {
            _hotkey.SetHotkey(cfg.HotkeyModifiers, cfg.HotkeyKey);
            _enabled.Checked = cfg.Enabled;
            _startup.Checked = cfg.StartWithWindows;
            _force.Checked = cfg.ForceTerminate;
            _hideHotkey.SetHotkey(cfg.HideHotkeyModifiers, cfg.HideHotkeyKey);
            _hideTray.Checked = cfg.HideTrayIcon;
            _hideTaskbar.Checked = cfg.HideTaskbarIcon;
            _hiddenNow.Checked = cfg.HiddenModeActive;
            _close.SetItems(cfg.CloseApps);
            _open.SetItems(cfg.OpenApps);
            _urls.SetItems(cfg.Urls);
        }

        public void SetEnabledCheckbox(bool value) { _enabled.Checked = value; }

        public void SetHiddenCheckbox(bool value) { _hiddenNow.Checked = value; }

        public void SetTaskbarVisible(bool visible)
        {
            if (ShowInTaskbar != visible) ShowInTaskbar = visible;
        }

        private AppConfig BuildConfig()
        {
            return new AppConfig
            {
                HotkeyModifiers = _hotkey.Modifiers,
                HotkeyKey = _hotkey.VirtualKey,
                Enabled = _enabled.Checked,
                ForceTerminate = _force.Checked,
                StartWithWindows = _startup.Checked,
                HideHotkeyModifiers = _hideHotkey.Modifiers,
                HideHotkeyKey = _hideHotkey.VirtualKey,
                HideTrayIcon = _hideTray.Checked,
                HideTaskbarIcon = _hideTaskbar.Checked,
                HiddenModeActive = _hiddenNow.Checked,
                CloseApps = _close.GetItems(),
                OpenApps = _open.GetItems(),
                Urls = _urls.GetItems()
            };
        }

        public void RefreshHotkeyStatus()
        {
            _hotkeyStatus.Text = _owner.HotkeyMessage ?? "";
            _hotkeyStatus.ForeColor = _owner.HotkeyOk ? Color.SeaGreen : Color.Firebrick;
            _hideHotkeyStatus.Text = _owner.HideHotkeyMessage ?? "";
            _hideHotkeyStatus.ForeColor = _owner.HideHotkeyOk ? Color.SeaGreen : Color.Firebrick;
        }

        private void SetStatus(string text, bool ok)
        {
            _status.Text = text;
            _status.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
        }

        private void OnSave(object s, EventArgs e)
        {
            bool saved = _owner.ApplyConfig(BuildConfig());
            RefreshHotkeyStatus();
            SetStatus(saved
                ? "Saved at " + DateTime.Now.ToString("HH:mm:ss") + "."
                : "Could not write the configuration file (see log).", saved);
        }

        private void OnRunningPrograms(object s, EventArgs e)
        {
            // Adding a program to one list removes it from the other, so a program
            // is never set to both close and open.
            using (var dlg = new RunningProgramsForm(
                list => { _open.RemoveByPath(list.Select(x => x.FilePath)); return _close.AddEntries(list); },
                list => { _close.RemoveByPath(list.Select(x => x.FilePath)); return _open.AddEntries(list); }))
            {
                dlg.ShowDialog(this);
            }
            SetStatus("Lists updated \u2014 click Save to apply.", true);
        }

        private void OnLoad(object s, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Load configuration";
                dlg.Filter = "JSON configuration (*.json)|*.json|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                AppConfig cfg = ConfigStore.Load(dlg.FileName);
                if (cfg == null)
                {
                    SetStatus("That file is not a valid AppSwitcher configuration.", false);
                    return;
                }
                LoadFromConfig(cfg);
                _owner.ApplyConfig(cfg);
                RefreshHotkeyStatus();
                SetStatus("Configuration loaded and applied.", true);
            }
        }

        private void OnSaveAs(object s, EventArgs e)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Save configuration";
                dlg.Filter = "JSON configuration (*.json)|*.json";
                dlg.FileName = "AppSwitcher-config.json";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                bool ok = ConfigStore.Save(BuildConfig(), dlg.FileName);
                SetStatus(ok ? "Configuration exported." : "Export failed (see log).", ok);
            }
        }

        public void BringUp()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true;
            TopMost = false;
            Activate();
            Native.SetForegroundWindow(Handle);
        }
    }

    // ------------------------------------------------------------------
    //  Tray + application lifetime
    // ------------------------------------------------------------------
    internal sealed class TrayController : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly HotkeyWindow _hk;
        private readonly Control _invoker;
        private readonly ToolStripMenuItem _miToggle;
        private readonly ToolStripMenuItem _miHide;
        private readonly EventWaitHandle _showEvent;
        private SettingsForm _form;
        private bool _exiting;
        private bool _hintShown;

        public AppConfig Config { get; private set; }
        public Icon AppIcon { get; private set; }
        public bool HotkeyOk { get; private set; }
        public string HotkeyMessage { get; private set; }
        public bool HideHotkeyOk { get; private set; }
        public string HideHotkeyMessage { get; private set; }

        public TrayController(bool showUi, EventWaitHandle showEvent)
        {
            _showEvent = showEvent;
            _invoker = new Control();
            IntPtr unused = _invoker.Handle; // force handle creation on the UI thread

            Config = ConfigStore.Load(AppPaths.ConfigFile) ?? new AppConfig();
            AppIcon = MakeIcon();

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Settings", null, (s, e) => ShowSettings());
            _miToggle = new ToolStripMenuItem("Disable Automation");
            _miToggle.Click += (s, e) => ToggleEnabled();
            menu.Items.Add(_miToggle);
            menu.Items.Add("Run Shortcut Actions", null, (s, e) => RunNow());
            _miHide = new ToolStripMenuItem("Hide Icons");
            _miHide.Click += (s, e) => SetHiddenMode(!Config.HiddenModeActive);
            menu.Items.Add(_miHide);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit Application", null, (s, e) => ExitApp());

            _tray = new NotifyIcon
            {
                Icon = AppIcon,
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, e) => ShowSettings();

            _hk = new HotkeyWindow();
            _hk.Pressed += OnHotkeyPressed;
            ApplyHotkey();
            ApplyHideHotkey();

            // Starting the exe by hand always brings hidden icons back.
            if (showUi && Config.HiddenModeActive)
            {
                Config.HiddenModeActive = false;
                ConfigStore.Save(Config, AppPaths.ConfigFile);
            }
            ApplyHiddenState();
            UpdateTray();

            // Second launches signal us through this event to show the settings window.
            var t = new Thread(() =>
            {
                while (true)
                {
                    try { _showEvent.WaitOne(); } catch { return; }
                    if (_exiting) return;
                    try { _invoker.BeginInvoke(new Action(ShowFromSecondLaunch)); } catch { }
                }
            });
            t.IsBackground = true;
            t.Name = "ShowSettingsListener";
            t.Start();

            if (showUi) ShowSettings();
        }

        private static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(Color.FromArgb(0, 120, 212)))
                        g.FillEllipse(b, 1, 1, 30, 30);
                    using (var f = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("S", f, Brushes.White, new RectangleF(0, 0, 32, 32), sf);
                }
                IntPtr h = bmp.GetHicon();
                try { using (var tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone(); }
                finally { Native.DestroyIcon(h); }
            }
        }

        private void ApplyHotkey()
        {
            if (!Config.Enabled)
            {
                _hk.Unregister();
                HotkeyOk = false;
                HotkeyMessage = "Automation is disabled \u2014 shortcut not registered.";
                return;
            }
            if (Config.HotkeyKey == 0 || Config.HotkeyModifiers == 0)
            {
                _hk.Unregister();
                HotkeyOk = false;
                HotkeyMessage = "No shortcut set.";
                return;
            }

            string err;
            string desc = HotkeyBox.Describe(Config.HotkeyModifiers, Config.HotkeyKey);
            if (_hk.Register(Config.HotkeyModifiers, Config.HotkeyKey, out err))
            {
                HotkeyOk = true;
                HotkeyMessage = "Shortcut active: " + desc;
            }
            else
            {
                HotkeyOk = false;
                HotkeyMessage = err;
                Log.Write("Hotkey registration failed (" + desc + "): " + err);
            }
        }

        private void UpdateTray()
        {
            _miToggle.Text = Config.Enabled ? "Disable Automation" : "Enable Automation";
            _miHide.Text = Config.HiddenModeActive ? "Show Icons" : "Hide Icons";
            string tip = "AppSwitcher - " + (Config.Enabled
                ? (HotkeyOk ? HotkeyBox.Describe(Config.HotkeyModifiers, Config.HotkeyKey) : "no active shortcut")
                : "disabled");
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        // Applies + persists a configuration. Returns false if the file could not be written.
        public bool ApplyConfig(AppConfig cfg)
        {
            Config = cfg;
            bool saved = ConfigStore.Save(cfg, AppPaths.ConfigFile);
            try { StartupManager.Set(cfg.StartWithWindows); }
            catch (Exception ex) { Log.Write("Startup registration failed", ex); }
            ApplyHotkey();
            ApplyHideHotkey();
            ApplyHiddenState();
            UpdateTray();
            return saved;
        }

        private void ToggleEnabled()
        {
            Config.Enabled = !Config.Enabled;
            ConfigStore.Save(Config, AppPaths.ConfigFile);
            ApplyHotkey();
            UpdateTray();
            if (_form != null && !_form.IsDisposed)
            {
                _form.SetEnabledCheckbox(Config.Enabled);
                _form.RefreshHotkeyStatus();
            }
        }

        private void OnHotkeyPressed(int id)
        {
            if (id == HotkeyWindow.HotkeyId)
            {
                if (Config.Enabled) RunNow();
            }
            else if (id == HotkeyWindow.HideHotkeyId)
            {
                SetHiddenMode(!Config.HiddenModeActive);
            }
        }

        // Registers the second global shortcut that hides / shows the icons.
        private void ApplyHideHotkey()
        {
            if (Config.HideHotkeyKey == 0 || Config.HideHotkeyModifiers == 0)
            {
                _hk.Unregister(HotkeyWindow.HideHotkeyId);
                HideHotkeyOk = false;
                HideHotkeyMessage = "No hide/show shortcut set.";
                return;
            }
            if (Config.HideHotkeyKey == Config.HotkeyKey && Config.HideHotkeyModifiers == Config.HotkeyModifiers)
            {
                _hk.Unregister(HotkeyWindow.HideHotkeyId);
                HideHotkeyOk = false;
                HideHotkeyMessage = "The hide/show shortcut must be different from the automation shortcut.";
                return;
            }

            string err;
            string desc = HotkeyBox.Describe(Config.HideHotkeyModifiers, Config.HideHotkeyKey);
            if (_hk.Register(HotkeyWindow.HideHotkeyId, Config.HideHotkeyModifiers, Config.HideHotkeyKey, out err))
            {
                HideHotkeyOk = true;
                HideHotkeyMessage = "Hide/show shortcut active: " + desc;
            }
            else
            {
                HideHotkeyOk = false;
                HideHotkeyMessage = err;
                Log.Write("Hide/show hotkey registration failed (" + desc + "): " + err);
            }
        }

        // Applies the current hidden-mode state to the tray icon and the settings window's taskbar button.
        private void ApplyHiddenState()
        {
            bool hidden = Config.HiddenModeActive;
            _tray.Visible = !(hidden && Config.HideTrayIcon);
            if (_form != null && !_form.IsDisposed)
                _form.SetTaskbarVisible(!(hidden && Config.HideTaskbarIcon));
        }

        public void SetHiddenMode(bool hidden)
        {
            Config.HiddenModeActive = hidden;
            ConfigStore.Save(Config, AppPaths.ConfigFile);
            ApplyHiddenState();
            UpdateTray();
            if (_form != null && !_form.IsDisposed) _form.SetHiddenCheckbox(hidden);
        }

        // Launching the exe a second time restores hidden icons and shows the settings.
        private void ShowFromSecondLaunch()
        {
            if (Config.HiddenModeActive) SetHiddenMode(false);
            ShowSettings();
        }

        public void RunNow()
        {
            AppConfig snapshot = Config.Clone();
            Task.Run(() => ActionRunner.RunAsync(snapshot));
        }

        public void ShowSettings()
        {
            if (_exiting) return;
            if (_form == null || _form.IsDisposed)
            {
                _form = new SettingsForm(this);
                ApplyHiddenState();
            }
            _form.BringUp();
        }

        public void NotifyHidden()
        {
            if (_hintShown || !_tray.Visible) return;
            _hintShown = true;
            _tray.ShowBalloonTip(2000, "AppSwitcher", "Still running in the system tray.", ToolTipIcon.Info);
        }

        private void ExitApp()
        {
            _exiting = true;
            try { _showEvent.Set(); } catch { }
            _hk.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            if (_form != null && !_form.IsDisposed)
            {
                _form.AllowClose = true;
                _form.Close();
                _form.Dispose();
            }
            ExitThread();
        }
    }

    // ------------------------------------------------------------------
    //  Entry point
    // ------------------------------------------------------------------
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool background = args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));

            // Named event used by a second launch to ask the running instance to show its settings.
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AppSwitcher.ShowSettings.v1"))
            {
                bool createdNew;
                using (var mutex = new Mutex(true, @"Local\AppSwitcher.SingleInstance.v1", out createdNew))
                {
                    if (!createdNew)
                    {
                        // Already running: bring its settings window forward (unless this is a startup launch).
                        if (!background) { try { showEvent.Set(); } catch { } }
                        return 0;
                    }

                    try
                    {
                        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                        Application.ThreadException += (s, e) => Log.Write("UI thread exception", e.Exception);
                        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                        {
                            var ex = e.ExceptionObject as Exception;
                            Log.Write(ex != null ? "Unhandled: " + ex : "Unhandled exception");
                        };

                        Application.SetHighDpiMode(HighDpiMode.SystemAware);
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);

                        using (var ctx = new TrayController(!background, showEvent))
                        {
                            Application.Run(ctx);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Fatal", ex);
                    }
                    finally
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
            return 0;
        }
    }
}
