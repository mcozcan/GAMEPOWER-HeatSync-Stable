// HeatSyncFix.dll - helper library used by the patched GAMEPOWER.exe (HeatSync-ArrowFix).
//
//  Lcd.ArrowLevel : replaces the free-running 0..7 animation counter that the original
//                   software wrote into bytes [10] (CPU side) and [11] (GPU side) of every
//                   LCD packet (command 169). The level is now derived from the real
//                   temperature, so the arrows stay still while the temperature is constant
//                   and move only when the temperature changes.
//  Diag           : optional (--debug) protocol logger for every buffer written to the LCD port.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyTitle("HeatSyncFix")]

namespace HeatSyncFix
{
    public static class Lcd
    {
        // Linear 0..100 °C scale (the same 0-100 °C -> 0-100 % convention the original software
        // uses for its temperature bars), split into the 8 values (0..7) the firmware accepts.
        public static float MinC = 0f, MaxC = 100f;
        public const int Levels = 8;
        public static float HysteresisC = 1f;      // avoids flicker when a sensor toggles at a step boundary
        static readonly int[] last = { -1, -1 };
        static readonly object lk = new object();

        static Lcd()
        {
            try
            {
                string ini = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HeatSync-ArrowFix.ini");
                if (!File.Exists(ini)) ini = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HeatSync-Stable.ini");   // previous name
                if (!File.Exists(ini)) return;
                foreach (var raw in File.ReadAllLines(ini))
                {
                    var line = raw.Trim();
                    if (line.StartsWith(";") || line.StartsWith("#") || !line.Contains("=")) continue;
                    var k = line.Substring(0, line.IndexOf('=')).Trim().ToLowerInvariant();
                    var v = line.Substring(line.IndexOf('=') + 1).Trim().Replace(',', '.');
                    float f;
                    if (!float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) continue;
                    if (k == "arrowminc") MinC = f;
                    else if (k == "arrowmaxc") MaxC = f;
                    else if (k == "hysteresisc") HysteresisC = Math.Max(0f, f);
                }
                if (MaxC <= MinC) { MinC = 0f; MaxC = 100f; }
            }
            catch { MinC = 0f; MaxC = 100f; }
        }

        static float StepC { get { return (MaxC - MinC) / Levels; } }

        public static int RawLevel(float tempC)
        {
            if (float.IsNaN(tempC) || float.IsInfinity(tempC)) return 0;
            int lv = (int)Math.Floor((tempC - MinC) / StepC);
            if (lv < 0) lv = 0;
            if (lv > Levels - 1) lv = Levels - 1;
            return lv;
        }

        // --lcd-test: show values 0..7 one after another (each for TestSecondsPerLevel s) on both sides,
        // so the user can see what each value looks like on the device. Same value range the original sent.
        public static int TestSecondsPerLevel = 0;
        static DateTime testStart;
        static int lastTestLevel = -1;
        public static void StartTest(int secondsPerLevel) { testStart = DateTime.Now; TestSecondsPerLevel = secondsPerLevel; }

        /// <param name="side">0 = CPU (packet byte 10), 1 = GPU (packet byte 11)</param>
        public static int ArrowLevel(int side, float tempC)
        {
            if (TestSecondsPerLevel > 0)
            {
                int t = (int)((DateTime.Now - testStart).TotalSeconds / TestSecondsPerLevel) % Levels;
                if (t != lastTestLevel) { lastTestLevel = t; AppLog.Write("LCD TEST: arrow value now " + t); }
                return t;
            }
            int raw = RawLevel(tempC);
            if (side < 0 || side > 1) return raw;
            lock (lk)
            {
                int prev = last[side];
                if (prev < 0 || raw == prev) { last[side] = raw; return raw; }
                float lower = MinC + prev * StepC, upper = MinC + (prev + 1) * StepC;
                bool farEnough = (raw > prev && tempC >= upper + HysteresisC) || (raw < prev && tempC < lower - HysteresisC)
                                 || Math.Abs(raw - prev) > 1;
                if (farEnough) last[side] = raw;
                return last[side];
            }
        }

        public static void Reset() { lock (lk) { last[0] = -1; last[1] = -1; } }
    }

    // Guard for the HWiNFO SDK "update sensors of device N" export (#216 of msvcr120d.dll = HWiNFO64.dll).
    // On this PC it crashed the original program 3 times (Event Log: AccessViolationException in nvml.dll,
    // called from ⅌.⃲(Int32)). The patched EXE calls SafeUpdate instead of the raw P/Invoke.
    public static class Sensors
    {
        [System.Runtime.InteropServices.DllImport("msvcr120d.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = "#216")]
        static extern void HwUpdate(int device);

        static readonly Dictionary<int, DateTime> pausedUntil = new Dictionary<int, DateTime>();
        public static int Failures;
        public static int PauseSeconds = 5;

        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        public static void SafeUpdate(int device)
        {
            DateTime until;
            lock (pausedUntil) { if (pausedUntil.TryGetValue(device, out until) && DateTime.Now < until) return; }
            try { HwUpdate(device); }
            catch (AccessViolationException ex) { OnFailure(device, ex); }
            catch (System.Runtime.InteropServices.SEHException ex) { OnFailure(device, ex); }
        }

        static void OnFailure(int device, Exception ex)
        {
            Failures++;
            lock (pausedUntil) pausedUntil[device] = DateTime.Now.AddSeconds(PauseSeconds);
            AppLog.Write("HWiNFO sensor update for device " + device + " failed (" + ex.GetType().Name + ": " + ex.Message +
                         "); last values kept, device skipped for " + PauseSeconds + " s. Total failures: " + Failures);
        }

        // test hook: runs an arbitrary action under the same corrupted-state guard
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        public static bool GuardTest(Action a)
        {
            try { a(); return true; }
            catch (AccessViolationException) { return false; }
        }
    }

    public static class AppLog
    {
        static readonly object lk = new object();
        public static void Write(string s)
        {
            lock (lk)
            {
                try
                {
                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "HeatSync_" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + s + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }

    public static class Diag
    {
        static StreamWriter log;
        static readonly object lk = new object();
        static string lastPacket;
        static DateTime lastStats = DateTime.Now;
        static int writes, packets169, changed169;

        public static bool Enabled { get { return log != null; } }

        public static void Enable(string dir)
        {
            lock (lk)
            {
                if (log != null) return;
                Directory.CreateDirectory(dir);
                log = new StreamWriter(Path.Combine(dir, "lcd_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log"), false, new UTF8Encoding(false));
                log.AutoFlush = true;
                log.WriteLine(T() + " LCD protocol log. ArrowLevel scale " + Lcd.MinC + ".." + Lcd.MaxC + " C, hysteresis " + Lcd.HysteresisC + " C");
            }
        }

        static string T() { return DateTime.Now.ToString("HH:mm:ss.fff"); }

        // Called by the patched serial write method (⃷.ℚ(byte[], bool)) for every buffer.
        public static void OnWrite(object self, byte[] data, bool flag)
        {
            if (log == null) return;
            try
            {
                lock (lk)
                {
                    writes++;
                    if (data != null && data.Length >= 6 && data[5] == 169)
                    {
                        packets169++;
                        int d0 = data[0], d1 = data[1], d2 = data[2], d3 = data[3], d4 = data[4];
                        int f1 = (d0 << 2) | (d1 >> 6), f2 = ((d1 & 0x3F) << 4) | (d2 >> 4);
                        int f3 = ((d2 & 0xF) << 6) | (d3 >> 2), f4 = ((d3 & 3) << 8) | d4;
                        var sb = new StringBuilder();
                        sb.Append("PKT169 hdr=" + f1 + "," + f2 + "," + f3 + "," + f4);
                        if (data.Length >= 14)
                            sb.Append(" cpuDigits=" + data[6] + data[7] + " gpuDigits=" + data[8] + data[9] + " b10=" + data[10] + " b11=" + data[11] + " b12=" + data[12] + " b13=" + data[13]);
                        sb.Append(" raw=" + BitConverter.ToString(data));
                        string s = sb.ToString();
                        if (s != lastPacket) { changed169++; log.WriteLine(T() + " " + s); lastPacket = s; }
                    }
                    else if (data != null)
                    {
                        log.WriteLine(T() + " WRITE len=" + data.Length + " head=" + BitConverter.ToString(data, 0, Math.Min(8, data.Length)));
                    }
                    if ((DateTime.Now - lastStats).TotalSeconds >= 10)
                    {
                        log.WriteLine(T() + " STATS last10s writes=" + writes + " packets169=" + packets169 + " changedPackets=" + changed169);
                        writes = packets169 = changed169 = 0; lastStats = DateTime.Now;
                    }
                }
            }
            catch { }
        }
    }
}
