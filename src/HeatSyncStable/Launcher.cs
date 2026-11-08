// HeatSync-Stable.exe - launcher/host for a fixed copy of the GAMEPOWER HeatSync software.
//  * first run: copies the needed files from the user's own installed GAMEPOWER (read-only, never modified)
//    into this folder and applies the IL fixes (CorePatcher) to the copy
//  * runs the patched copy from THIS folder (own config\), never starts the original installation
//  * offers to close a running original (both cannot share the LCD COM port)
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
using System.Xml;

[assembly: AssemblyTitle("HeatSync-Stable")]
[assembly: AssemblyProduct("HeatSync-Stable")]
[assembly: AssemblyDescription("Fixed launcher for the GAMEPOWER HeatSync A-RGB LCD software")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace HeatSyncStable
{
    static class Program
    {
        const string Title = "HeatSync-Stable";
        const string TaskName = "UsbMonitor";
        // The patched core is stored as usb_hid.dll (its assembly name) so it cannot be double-clicked and run
        // as a second, unmanaged instance next to the launcher.
        const string CoreFile = "usb_hid.dll";
        const string DefaultInstall = @"C:\Program Files (x86)\GAMEPOWER\GAMEPOWER\GAMEPOWER";
        static readonly string[] CopiedFiles = { "msvcr120d.dll", "msvcr110d.dll", "NAudio.dll", "Microsoft.Win32.TaskScheduler.dll" };
        static string dir, logPath;
        static readonly object lk = new object();
        static Assembly core;

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
            Log("==== HeatSync-Stable " + Assembly.GetExecutingAssembly().GetName().Version + " start. folder=" + dir + " args=" + string.Join(" ", args));

            bool created;
            using (var mutex = new Mutex(true, "Global\\HeatSync-Stable-single-instance", out created))
            {
                if (!created)
                {
                    Log("Another HeatSync-Stable instance is running -> exit");
                    if (!auto) MessageBox.Show("HeatSync-Stable zaten çalışıyor (sistem tepsisinde).", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }

                // 1) no other GAMEPOWER may run: it would hold the LCD COM port (and lock files during setup)
                if (!HandleOtherInstances(auto)) return 3;

                // 2) patched copy present? otherwise build it from the installed original
                if (!EnsureCore(setup, auto)) return 2;

                // 3) optional: point the logon task to this copy (asked once)
                if (!auto) OfferAutostart();

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
            }
            return 0;
        }

        // ---------------------------------------------------------------- setup
        static bool CoreReady()
        {
            string coreDll = Path.Combine(dir, CoreFile);
            if (!File.Exists(coreDll) || !CorePatcher.IsPatched(coreDll)) return false;
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
                MessageBox.Show("HeatSync-Stable'ı kurulu GAMEPOWER klasörünün içine değil, ayrı bir klasöre çıkarın.", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                Log("Patched copy created");
                return true;
            }
            catch (Exception ex)
            {
                Log("SETUP FAILED: " + ex);
                MessageBox.Show("Düzeltilmiş kopya oluşturulamadı:\r\n\r\n" + ex.Message + "\r\n\r\nAyrıntı: " + logPath, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
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
            MessageBox.Show("HeatSync-Stable başlatılamadı:\r\n\r\n" + ex.GetType().Name + ": " + ex.Message +
                            "\r\n\r\nAyrıntılı kayıt: " + logPath, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static bool inDialog; static DateTime lastDialog = DateTime.MinValue;

        // A previously generated GAMEPOWER.exe in this folder (older HeatSync-Stable layout) is removed so it cannot be started directly.
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
        static void WatchOtherInstances()
        {
            var asked = new HashSet<int>();
            while (true)
            {
                Thread.Sleep(3000);
                try
                {
                    foreach (var p in Process.GetProcessesByName("GAMEPOWER"))
                    {
                        if (asked.Contains(p.Id)) continue;
                        asked.Add(p.Id);
                        string path = SafePath(p) ?? "?";
                        Log("Another GAMEPOWER started while running: pid=" + p.Id + " path=" + path);
                        var r = MessageBox.Show("Başka bir GAMEPOWER programı açıldı:\r\n" + path + "\r\n\r\n" +
                            "İki program LCD'yi aynı anda kullanırsa ekran ve program bozulabilir. O program kapatılsın mı?",
                            Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
                        if (r == DialogResult.Yes) { try { p.Kill(); Log("Closed pid=" + p.Id); } catch (Exception ex) { Log("Could not close pid=" + p.Id + ": " + ex.Message); } }
                    }
                }
                catch (Exception ex) { Log("watch error: " + ex.Message); }
            }
        }

        static string SafePath(Process p) { try { return p.MainModule.FileName; } catch { return null; } }

        static bool HandleOtherInstances(bool auto)
        {
            var self = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcesses())
            {
                if (p.Id == self.Id || !p.ProcessName.Equals("GAMEPOWER", StringComparison.OrdinalIgnoreCase)) continue;
                string path = SafePath(p) ?? "?";
                Log("Found running GAMEPOWER pid=" + p.Id + " path=" + path);
                var r = auto ? DialogResult.Yes : MessageBox.Show(
                    "Başka bir GAMEPOWER programı çalışıyor:\r\n" + path + "\r\n\r\n" +
                    "LCD'yi (COM portunu) iki program aynı anda kullanamaz. Bu program kapatılıp HeatSync-Stable başlatılsın mı?",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) { Log("User declined closing other instance -> exit"); return false; }
                try { p.Kill(); p.WaitForExit(5000); Log("Closed pid=" + p.Id); Thread.Sleep(1500); }
                catch (Exception ex)
                {
                    Log("Could not close pid=" + p.Id + ": " + ex.Message);
                    MessageBox.Show("Diğer GAMEPOWER kapatılamadı: " + ex.Message + "\r\nLütfen sistem tepsisinden kapatıp tekrar deneyin.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            return true;
        }

        // Logon task "UsbMonitor" (created by the original software) -> optionally point it to this copy.
        static void OfferAutostart()
        {
            string marker = Path.Combine(dir, "logs", "autostart_asked.txt");
            bool alreadyAsked = File.Exists(marker);
            try
            {
                string xml = RunSchtasks("/Query /TN \"" + TaskName + "\" /XML");
                if (xml == null) return;
                var doc = new XmlDocument(); doc.LoadXml(xml);
                var ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
                var cmd = doc.SelectSingleNode("//t:Actions/t:Exec/t:Command", ns);
                if (cmd == null) return;
                var argsNode = doc.SelectSingleNode("//t:Actions/t:Exec/t:Arguments", ns);
                string me = Path.Combine(dir, "HeatSync-Stable.exe");
                string current = cmd.InnerText.Trim('"');
                if (current.Equals(me, StringComparison.OrdinalIgnoreCase)) return;
                bool repairOwn = current.Equals(Path.Combine(dir, "GAMEPOWER.exe"), StringComparison.OrdinalIgnoreCase);
                if (repairOwn)
                {
                    Log("Logon task points to this folder's old core exe -> repointing to " + me);
                    RepointTask(doc, ns, cmd, argsNode, me);
                    return;
                }
                if (alreadyAsked) return;
                Log("Logon task '" + TaskName + "' currently starts: " + current + " " + (argsNode != null ? argsNode.InnerText : ""));
                var r = MessageBox.Show(
                    "Windows açılışında şu an başka bir program başlatılıyor (Görev Zamanlayıcı: \"" + TaskName + "\"):\r\n" + current +
                    "\r\n\r\nAçılışta bunun yerine HeatSync-Stable başlatılsın mı?\r\n(Orijinal kurulum dosyalarına dokunulmaz; yalnızca bu görevin hedefi değişir. Bu soru bir daha sorulmaz.)",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                File.WriteAllText(marker, DateTime.Now + " answer=" + r + " previousTarget=" + current + " " + (argsNode != null ? argsNode.InnerText : ""));
                if (r != DialogResult.Yes) return;
                RepointTask(doc, ns, cmd, argsNode, me);
            }
            catch (Exception ex) { Log("Autostart check failed: " + ex.Message); }
        }

        static void RepointTask(XmlDocument doc, XmlNamespaceManager ns, XmlNode cmd, XmlNode argsNode, string me)
        {
            try
            {
                cmd.InnerText = me;
                if (argsNode == null) { argsNode = doc.CreateElement("Arguments", ns.LookupNamespace("t")); cmd.ParentNode.AppendChild(argsNode); }
                argsNode.InnerText = "-auto";
                var wd = doc.SelectSingleNode("//t:Actions/t:Exec/t:WorkingDirectory", ns);
                if (wd == null) { wd = doc.CreateElement("WorkingDirectory", ns.LookupNamespace("t")); cmd.ParentNode.AppendChild(wd); }
                wd.InnerText = dir;
                string tmp = Path.Combine(Path.GetTempPath(), "HeatSync-Stable-task.xml");
                File.WriteAllText(tmp, doc.OuterXml, Encoding.Unicode);
                RunSchtasks("/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
                File.Delete(tmp);
                Log("Logon task now starts " + me + " -auto");
            }
            catch (Exception ex) { Log("Autostart check failed: " + ex.Message); }
        }

        static string RunSchtasks(string arguments)
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.Unicode };
            if (arguments.StartsWith("/Query")) psi.StandardOutputEncoding = Encoding.Default;
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
                return p.ExitCode == 0 ? o : null;
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
