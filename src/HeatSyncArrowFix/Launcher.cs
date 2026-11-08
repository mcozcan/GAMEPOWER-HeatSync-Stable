// HeatSync-ArrowFix.exe - launcher/host for a fixed copy of the GAMEPOWER HeatSync software.
//  * first run: copies the needed files from the user's own installed GAMEPOWER (read-only, never modified)
//    into this folder and applies the IL fixes (CorePatcher) to the copy
//  * runs the patched copy from THIS folder (own config\), never starts the original installation
//  * asks the user to close a running original from its tray icon (both cannot share the LCD COM port)
//  * does NOT create/modify scheduled tasks and does NOT terminate other processes
//  * logs unhandled exceptions with full stack traces to logs\HeatSync_yyyyMMdd.log
//  * --debug     : also logs the app's console output, first-chance exceptions and every LCD packet
//  * --lcd-test  : shows arrow values 0..7, 5 s each
//  * --setup     : re-create the patched copy from the installed original
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("HeatSync-ArrowFix - LCD arrow fix for GAMEPOWER HeatSync")]
[assembly: AssemblyProduct("HeatSync-ArrowFix")]
[assembly: AssemblyDescription("Open-source launcher: runs a locally patched copy of the GAMEPOWER HeatSync LCD software. https://github.com/mcozcan/Gamepower-HeatSync-ArrowFix")]
[assembly: AssemblyCompany("Mehmet Can Özcan (open source, MIT)")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Mehmet Can Özcan - MIT License")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1.0")]

namespace HeatSyncArrowFix
{
    static class Program
    {
        const string Title = "HeatSync-ArrowFix";
        // The patched core is stored as usb_hid.dll (its assembly name) so it cannot be double-clicked and run
        // as a second, unmanaged instance next to the launcher.
        const string CoreFile = "usb_hid.dll";
        const string ShowEventName = "HeatSync-ArrowFix-show-settings";
        static EventWaitHandle showEvent;
        static bool freshSetup;
        const string DefaultInstall = @"C:\Program Files (x86)\GAMEPOWER\GAMEPOWER\GAMEPOWER";
        static readonly string[] CopiedFiles = { "msvcr120d.dll", "msvcr110d.dll", "NAudio.dll", "Microsoft.Win32.TaskScheduler.dll" };
        static string dir, logPath;
        static readonly object lk = new object();
        static Assembly core;

        public static void LogPublic(string s) { Log(s); }

        static void Log(string s)
        {
            lock (lk)
            {
                try { File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + s + Environment.NewLine, Encoding.UTF8); } catch { }
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            Directory.SetCurrentDirectory(dir);
            string logDir = Path.Combine(dir, "logs");
            try { Directory.CreateDirectory(logDir); } catch { }
            logPath = Path.Combine(logDir, "HeatSync_" + DateTime.Now.ToString("yyyyMMdd") + ".log");

            bool debug = false, auto = false, setup = false; int exitAfter = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--debug") debug = true;
                else if (args[i] == "-auto") auto = true;
                else if (args[i] == "--setup") setup = true;
                else if (args[i] == "--exit-after" && i + 1 < args.Length) int.TryParse(args[++i], out exitAfter);
                else if (args[i] == "--lcd-test") { HeatSyncFix.Lcd.StartTest(5); debug = true; }
            }
            Log("==== HeatSync-ArrowFix " + Assembly.GetExecutingAssembly().GetName().Version + " start. folder=" + dir + " args=" + string.Join(" ", args));

            bool created;
            bool restart = Array.IndexOf(args, "--restart") >= 0;
            Mutex mutex0 = new Mutex(true, "HeatSync-ArrowFix-single-instance", out created);
            for (int i = 0; restart && !created && i < 40; i++)
            {
                mutex0.Dispose(); Thread.Sleep(500);
                mutex0 = new Mutex(true, "HeatSync-ArrowFix-single-instance", out created);
            }
            using (var mutex = mutex0)
            {
                if (!created)
                {
                    // already running: ask the running instance to show its settings window
                    Log("Another HeatSync-ArrowFix instance is running -> asking it to show settings");
                    bool signalled = false;
                    try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) { ev.Set(); signalled = true; } } catch { }
                    if (!signalled && !auto) MessageBox.Show("HeatSync-ArrowFix zaten çalışıyor (sistem tepsisinde).", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }
                showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

                // 1) no other GAMEPOWER may run: it would hold the LCD COM port (and lock files during setup)
                if (!HandleOtherInstances(auto)) return 3;

                // 2) patched copy present? otherwise build it from the installed original
                if (!EnsureCore(setup, auto)) return 2;

                // 4) exception logging
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => OnUiException(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("UNHANDLED (terminating=" + e.IsTerminating + "): " + e.ExceptionObject);
                if (debug)
                {
                    HeatSyncFix.Diag.Enable(logDir);
                    Console.SetOut(new LineWriter());
                    AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
                    {
                        try { Log("first-chance " + e.Exception.GetType().FullName + ": " + e.Exception.Message + "\r\n" + e.Exception.StackTrace); } catch { }
                    };
                }
                else Console.SetOut(TextWriter.Null);

                new Thread(WatchOtherInstances) { IsBackground = true, Name = "instance-watch" }.Start();
                // The tray icon is a window: it may only be created after the core has called
                // Application.SetCompatibleTextRenderingDefault, i.e. once its message loop is running.
                EventHandler onIdle = null;
                onIdle = (s, e) => { Application.Idle -= onIdle; CreateTray(); };
                Application.Idle += onIdle;

                if (exitAfter > 0)
                    new Thread(() => { Thread.Sleep(exitAfter * 1000); Log("--exit-after reached"); Environment.Exit(0); }) { IsBackground = true }.Start();

                // 5) load and run the patched core from this folder
                AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
                {
                    var n = new AssemblyName(e.Name).Name;
                    if (n == "usb_hid") return core;
                    string p = Path.Combine(dir, n + ".dll");
                    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
                };
                try
                {
                    RemoveOldCoreExe();
                    core = Assembly.Load(new AssemblyName("usb_hid"));
                    if (!string.Equals(Path.GetFullPath(core.Location), Path.Combine(dir, CoreFile), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Unexpected core location: " + core.Location);
                    Log("Core loaded: " + core.Location + " | arrow scale " + HeatSyncFix.Lcd.MinC + ".." + HeatSyncFix.Lcd.MaxC + " C");
                    var ep = core.EntryPoint;
                    ep.Invoke(null, ep.GetParameters().Length == 0 ? null : new object[] { args });
                }
                catch (TargetInvocationException tie) { Fatal(tie.InnerException ?? tie); return 4; }
                catch (Exception ex) { Fatal(ex); return 4; }
                Log("Core exited normally");
                try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
            }
            return 0;
        }

        // ---------------------------------------------------------------- setup
        static bool CoreReady()
        {
            string coreDll = Path.Combine(dir, CoreFile);
            if (!File.Exists(coreDll) || !CorePatcher.IsPatched(coreDll)) return false;
            if (ReadPatchVersion() != CorePatcher.PatchVersion) return false;
            foreach (var f in CopiedFiles) if (!File.Exists(Path.Combine(dir, f))) return false;
            return File.Exists(Path.Combine(dir, "HeatSyncFix.dll")) && Directory.Exists(Path.Combine(dir, "config"));
        }

        static string Sha256(string path)
        {
            using (var s = SHA256.Create()) using (var f = File.OpenRead(path))
                return BitConverter.ToString(s.ComputeHash(f)).Replace("-", "");
        }

        static bool EnsureCore(bool force, bool auto)
        {
            if (!force && CoreReady()) return true;
            if (!force && TryUpgradeFromBackup()) return true;
            if (!File.Exists(Path.Combine(dir, "HeatSyncFix.dll")) || !File.Exists(Path.Combine(dir, "dnlib.dll")))
            {
                MessageBox.Show("HeatSyncFix.dll / dnlib.dll bulunamadı. İndirdiğiniz ZIP'in tamamını aynı klasöre çıkarın.\r\n\r\nKlasör: " + dir,
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            if (auto) { Log("Patched copy missing in -auto mode -> exit"); return false; }

            string src = DefaultInstall;
            if (!File.Exists(Path.Combine(src, "GAMEPOWER.exe")))
            {
                using (var fb = new FolderBrowserDialog { Description = "Kurulu GAMEPOWER klasörünü seçin (içinde GAMEPOWER.exe olan klasör)" })
                {
                    if (fb.ShowDialog() != DialogResult.OK) return false;
                    src = fb.SelectedPath;
                }
            }
            string srcExe = Path.Combine(src, "GAMEPOWER.exe");
            if (!File.Exists(srcExe))
            {
                MessageBox.Show("Seçilen klasörde GAMEPOWER.exe yok:\r\n" + src + "\r\n\r\nÖnce üreticinin GAMEPOWER HeatSync yazılımını kurun.", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            if (Path.GetFullPath(src).TrimEnd('\\').Equals(dir, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("HeatSync-ArrowFix'ı kurulu GAMEPOWER klasörünün içine değil, ayrı bir klasöre çıkarın.", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            string hash = Sha256(srcExe);
            Log("Original: " + srcExe + " sha256=" + hash);
            if (hash != CorePatcher.SupportedSha256)
            {
                MessageBox.Show("Bu GAMEPOWER sürümü desteklenmiyor.\r\n\r\nDesteklenen: GAMEPOWER.exe 2.2.1.0\r\nSHA-256 " + CorePatcher.SupportedSha256 +
                    "\r\n\r\nBulunan: " + hash + "\r\n\r\nLütfen GitHub sayfasında bir 'issue' açın.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            var r = MessageBox.Show("Kurulu GAMEPOWER bulundu:\r\n" + src + "\r\n\r\n" +
                "Gerekli dosyalar bu klasöre kopyalanıp düzeltilmiş bir kopya oluşturulacak:\r\n" + dir + "\r\n\r\n" +
                "Orijinal kurulum DEĞİŞTİRİLMEZ. Devam edilsin mi?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return false;

            try
            {
                string backup = Path.Combine(dir, "_yedek_orijinal");
                Directory.CreateDirectory(Path.Combine(backup, "config"));
                foreach (var f in new[] { "GAMEPOWER.exe" }.Concat2(CopiedFiles))
                {
                    var from = Path.Combine(src, f);
                    if (!File.Exists(from)) throw new FileNotFoundException("Kurulumda eksik dosya: " + from);
                    CopyNormal(from, Path.Combine(backup, f));
                    if (f != "GAMEPOWER.exe") CopyNormal(from, Path.Combine(dir, f));
                }
                Directory.CreateDirectory(Path.Combine(dir, "config"));
                foreach (var f in Directory.GetFiles(Path.Combine(src, "config"), "*.data"))
                {
                    CopyNormal(f, Path.Combine(backup, "config", Path.GetFileName(f)));
                    var to = Path.Combine(dir, "config", Path.GetFileName(f));
                    if (!File.Exists(to)) CopyNormal(f, to);          // never overwrite this copy's own settings
                }
                // "app" marker: prevents the core from silently re-registering the logon task on first run
                File.WriteAllText(Path.Combine(dir, "app"), "");
                var steps = CorePatcher.Patch(Path.Combine(backup, "GAMEPOWER.exe"), Path.Combine(dir, CoreFile));
                RemoveOldCoreExe();
                foreach (var s in steps) Log("patch: " + s);
                WritePatchVersion();
                Log("Patched copy created (patch version " + CorePatcher.PatchVersion + ")");
                freshSetup = true;
                return true;
            }
            catch (Exception ex)
            {
                Log("SETUP FAILED: " + ex);
                MessageBox.Show("Düzeltilmiş kopya oluşturulamadı:\r\n\r\n" + ex.Message + "\r\n\r\nAyrıntı: " + logPath, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        static string PatchMarker { get { return Path.Combine(dir, "usb_hid.patch"); } }
        static int ReadPatchVersion() { try { return int.Parse(File.ReadAllText(PatchMarker).Trim()); } catch { return 1; } }
        static void WritePatchVersion() { File.WriteAllText(PatchMarker, CorePatcher.PatchVersion.ToString()); }

        // An existing copy made by an older HeatSync-ArrowFix: re-patch silently from this folder's own verified backup.
        static bool TryUpgradeFromBackup()
        {
            try
            {
                string bak = Path.Combine(dir, "_yedek_orijinal", "GAMEPOWER.exe");
                if (!File.Exists(Path.Combine(dir, CoreFile)) || !File.Exists(bak) || !Directory.Exists(Path.Combine(dir, "config"))) return false;
                foreach (var f in CopiedFiles) if (!File.Exists(Path.Combine(dir, f))) return false;
                if (Sha256(bak) != CorePatcher.SupportedSha256) return false;
                var steps = CorePatcher.Patch(bak, Path.Combine(dir, CoreFile));
                foreach (var st in steps) Log("upgrade patch: " + st);
                WritePatchVersion();
                Log("Patched copy upgraded to patch version " + CorePatcher.PatchVersion);
                return true;
            }
            catch (Exception ex) { Log("Upgrade from backup failed: " + ex); return false; }
        }

        static IEnumerable<string> Concat2(this string[] a, string[] b) { foreach (var s in a) yield return s; foreach (var s in b) yield return s; }

        static void CopyNormal(string from, string to)
        {
            if (File.Exists(to) && new FileInfo(to).Length == new FileInfo(from).Length && Sha256(to) == Sha256(from)) return;   // already identical
            if (File.Exists(to)) File.SetAttributes(to, FileAttributes.Normal);
            File.Copy(from, to, true);
            File.SetAttributes(to, FileAttributes.Normal);
        }

        // ---------------------------------------------------------------- runtime helpers
        static bool IsLcdNotConnected(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is IOException && e.Message.StartsWith("LCD serial port is not open")) return true;
            return false;
        }

        static void OnUiException(Exception ex)
        {
            if (IsLcdNotConnected(ex)) { Log("LCD not connected yet (handled, no dialog): " + ex.Message); return; }
            Log("UI EXCEPTION: " + ex);
            // never let the error dialog itself take the process down; at most one dialog per 5 minutes,
            // none for window-handle/GDI exhaustion (a dialog cannot be created in that state anyway)
            if (ex is System.ComponentModel.Win32Exception || inDialog || (DateTime.Now - lastDialog).TotalMinutes < 5) return;
            inDialog = true; lastDialog = DateTime.Now;
            try
            {
                MessageBox.Show("Beklenmeyen bir hata oluştu ve kaydedildi; program çalışmaya devam ediyor.\r\n\r\n" +
                                ex.GetType().Name + ": " + ex.Message + "\r\n\r\nAyrıntılı kayıt (stack trace):\r\n" + logPath,
                                Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception dlgEx) { Log("error dialog failed: " + dlgEx.GetType().Name + " " + dlgEx.Message); }
            finally { inDialog = false; }
        }

        static void Fatal(Exception ex)
        {
            Log("FATAL: " + ex);
            MessageBox.Show("HeatSync-ArrowFix başlatılamadı:\r\n\r\n" + ex.GetType().Name + ": " + ex.Message +
                            "\r\n\r\nAyrıntılı kayıt: " + logPath, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static bool inDialog; static DateTime lastDialog = DateTime.MinValue;

        // ---------------------------------------------------------------- own tray icon: arrow source choice
        static NotifyIcon tray;
        static ToolStripMenuItem miTemp, miLoad;

        static void CreateTray()
        {
            try
            {
                var menu = new ContextMenuStrip();
                var settingsItem = new ToolStripMenuItem("Ayarlar…", null, (s, e) => ShowSettings());
                settingsItem.Font = new System.Drawing.Font(settingsItem.Font, System.Drawing.FontStyle.Bold);
                menu.Items.Add(settingsItem);
                menu.Items.Add(new ToolStripSeparator());
                var head = new ToolStripMenuItem("LCD okları neye göre hareket etsin?") { Enabled = false };
                miTemp = new ToolStripMenuItem("Sıcaklığa göre (CPU / GPU °C)", null, (s, e) => SetArrowSource(false));
                miLoad = new ToolStripMenuItem("Kullanım yüzdesine göre (CPU / GPU %)", null, (s, e) => SetArrowSource(true));
                menu.Items.Add(head); menu.Items.Add(miTemp); menu.Items.Add(miLoad);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Kayıt klasörünü aç", null, (s, e) => { try { Process.Start("explorer.exe", "\"" + Path.Combine(dir, "logs") + "\""); } catch { } });
                menu.Items.Add("GitHub sayfası", null, (s, e) => { try { Process.Start("https://github.com/mcozcan/Gamepower-HeatSync-ArrowFix"); } catch { } });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Çıkış", null, (s, e) => ExitProgram());
                tray = new NotifyIcon
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),
                    Text = "HeatSync-ArrowFix",
                    ContextMenuStrip = menu,
                    Visible = true
                };
                tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
                UpdateTrayChecks();
                Log("Tray icon created; arrows: " + HeatSyncFix.Lcd.Describe());
                uiContext = SynchronizationContext.Current;
                if (showEvent != null)
                    new Thread(() => { while (true) { showEvent.WaitOne(); try { uiContext.Post(_ => ShowSettings(), null); } catch { } } }) { IsBackground = true, Name = "show-settings" }.Start();
                string shownMarker = Path.Combine(dir, "logs", "settings_shown.txt");
                if (freshSetup || !File.Exists(shownMarker))
                {
                    try { File.WriteAllText(shownMarker, DateTime.Now.ToString()); } catch { }
                    ShowSettings();
                }
                Application.ApplicationExit += (s, e) => { try { tray.Visible = false; tray.Dispose(); } catch { } };
            }
            catch (Exception ex) { Log("tray failed: " + ex.Message); }
        }

        static SynchronizationContext uiContext;
        static SettingsForm settings;

        static void ShowSettings()
        {
            try
            {
                if (settings == null || settings.IsDisposed)
                {
                    settings = new SettingsForm(tray != null ? tray.Icon : null, new SettingsForm.Bridge
                    {
                        SetSource = SetArrowSource,
                        SetMinLevel = lv => { HeatSyncFix.Lcd.SetMinLevel(lv); SaveIniValue("MinLevel", lv.ToString()); },
                        GetGpus = GetGpuList,
                        GetSelectedGpu = GetSelectedGpu,
                        ApplyGpu = ApplyGpuAndRestart,
                        GetAutostart = Autostart.IsEnabled,
                        SetAutostart = Autostart.Set
                    });
                    settings.Show();
                }
                if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
                settings.TopMost = true; settings.Activate(); settings.TopMost = false;
            }
            catch (Exception ex) { Log("settings window failed: " + ex); }
        }

        // ---- GPU selection (core internals: ⅌.℟ = HWiNFO devices containing "GPU", ℆.ℕ = AppConfig, ℆.⃼() = save AppConfig)
        static List<string> GetGpuList()
        {
            var t = core.GetType("\u214C");
            var fld = t == null ? null : t.GetField("\u211F", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var list = fld == null ? null : fld.GetValue(null) as List<string>;
            return list == null ? new List<string>() : new List<string>(list);
        }

        static object AppConfigObj()
        {
            var t = core.GetType("\u2106");
            var fld = t.GetField("\u2115", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return fld.GetValue(null);
        }

        static string GetSelectedGpu()
        {
            var cfg = AppConfigObj();
            return cfg == null ? null : cfg.GetType().GetProperty("GpuName").GetValue(cfg, null) as string;
        }

        static void ApplyGpuAndRestart(string gpu)
        {
            try
            {
                var cfg = AppConfigObj();
                cfg.GetType().GetProperty("GpuName").SetValue(cfg, gpu, null);
                MethodInfo save = null;
                foreach (var m in core.GetType("\u2106").GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == "\u20FC" && m.GetParameters().Length == 0 && m.ReturnType == typeof(void)) save = m;
                if (save == null) throw new MissingMethodException("AppConfig save method not found");
                save.Invoke(null, null);
                Log("GPU set to '" + gpu + "' and saved; restarting");
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--restart") { WorkingDirectory = dir, UseShellExecute = true });
                try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Log("GPU change failed: " + ex);
                MessageBox.Show("GPU değiştirilemedi: " + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static void ExitProgram()
        {
            Log("Exit requested from tray menu");
            try { if (settings != null && !settings.IsDisposed) settings.Close(); } catch { }
            try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
            Environment.Exit(0);
        }

        static void UpdateTrayChecks()
        {
            miTemp.Checked = !HeatSyncFix.Lcd.UseLoad; miLoad.Checked = HeatSyncFix.Lcd.UseLoad;
            tray.Text = "HeatSync-ArrowFix - oklar: " + (HeatSyncFix.Lcd.UseLoad ? "kullanım %" : "sıcaklık");
        }

        static void SetArrowSource(bool useLoad)
        {
            HeatSyncFix.Lcd.SetSource(useLoad);
            UpdateTrayChecks();
            if (settings != null && !settings.IsDisposed) settings.SyncChoice();
            SaveIniValue("ArrowSource", useLoad ? "Load" : "Temp");
            tray.ShowBalloonTip(2500, "HeatSync-ArrowFix", "LCD okları artık " + (useLoad ? "kullanım yüzdesine" : "sıcaklığa") + " göre hareket ediyor.", ToolTipIcon.Info);
        }

        static void SaveIniValue(string key, string value)
        {
            try
            {
                string ini = Path.Combine(dir, "HeatSync-ArrowFix.ini");
                var lines = File.Exists(ini) ? new List<string>(File.ReadAllLines(ini)) : new List<string>();
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    var l = lines[i].TrimStart();
                    if (l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) || l.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase)) { lines[i] = key + "=" + value; found = true; }
                }
                if (!found) lines.Insert(0, key + "=" + value);
                File.WriteAllLines(ini, lines.ToArray());
            }
            catch (Exception ex) { Log("ini save failed: " + ex.Message); }
        }

        // A previously generated GAMEPOWER.exe in this folder (older HeatSync-ArrowFix layout) is removed so it cannot be started directly.
        static void RemoveOldCoreExe()
        {
            string old = Path.Combine(dir, "GAMEPOWER.exe");
            try
            {
                if (File.Exists(old) && CorePatcher.IsPatched(old))
                {
                    File.SetAttributes(old, System.IO.FileAttributes.Normal); File.Delete(old);
                    Log("Removed old directly-startable core " + old);
                }
            }
            catch (Exception ex) { Log("Could not remove old core exe: " + ex.Message); }
        }

        // Runs for the whole session: another GAMEPOWER (original or anything else) would fight for the LCD COM port.
        // Only informs the user; never terminates other processes.
        static void WatchOtherInstances()
        {
            var warned = new HashSet<int>();
            while (true)
            {
                Thread.Sleep(3000);
                try
                {
                    foreach (var p in Process.GetProcessesByName("GAMEPOWER"))
                    {
                        if (!warned.Add(p.Id)) continue;
                        Log("Another GAMEPOWER started while running: pid=" + p.Id + " path=" + (SafePath(p) ?? "?"));
                        MessageBox.Show("Başka bir GAMEPOWER programı açıldı.\r\n\r\n" +
                            "İki program LCD'yi aynı anda kullanamaz. Lütfen o programı sistem tepsisindeki simgesine sağ tıklayıp kapatın.",
                            Title, MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
                    }
                }
                catch (Exception ex) { Log("watch error: " + ex.Message); }
            }
        }

        static string SafePath(Process p) { try { return p.MainModule.FileName; } catch { return null; } }

        // Another GAMEPOWER would hold the LCD COM port (and lock files during setup): ask the user to close it.
        static bool HandleOtherInstances(bool auto)
        {
            while (true)
            {
                var self = Process.GetCurrentProcess();
                var others = new List<Process>();
                foreach (var p in Process.GetProcessesByName("GAMEPOWER")) if (p.Id != self.Id) others.Add(p);
                if (others.Count == 0) return true;
                foreach (var p in others) Log("Found running GAMEPOWER pid=" + p.Id + " path=" + (SafePath(p) ?? "?"));
                if (auto) { Log("Another GAMEPOWER is running in -auto mode -> exit"); return false; }
                var r = MessageBox.Show(
                    "Başka bir GAMEPOWER programı çalışıyor:\r\n" + (SafePath(others[0]) ?? "GAMEPOWER.exe") + "\r\n\r\n" +
                    "LCD'yi (COM portunu) iki program aynı anda kullanamaz.\r\n" +
                    "Lütfen saatin yanındaki sistem tepsisinde GAMEPOWER simgesine sağ tıklayıp kapatın, sonra \"Yeniden Dene\"ye basın.",
                    Title, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.Retry) { Log("User cancelled while another GAMEPOWER was running -> exit"); return false; }
                Thread.Sleep(1000);
            }
        }
        sealed class LineWriter : TextWriter
        {
            readonly StringBuilder buf = new StringBuilder();
            public override Encoding Encoding { get { return Encoding.UTF8; } }
            public override void Write(char c)
            {
                lock (buf)
                {
                    if (c == '\n') { var s = buf.ToString().TrimEnd('\r'); buf.Length = 0; if (s.Length > 0) Log("[app] " + s); }
                    else buf.Append(c);
                }
            }
            public override void Write(string s) { if (s != null) foreach (var c in s) Write(c); }
            public override void WriteLine(string s) { Write(s); Write('\n'); }
        }
    }
}
