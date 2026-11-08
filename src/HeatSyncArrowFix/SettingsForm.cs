// Settings window: arrow source (temperature / usage), minimum one arrow, GPU selection, live values.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HeatSyncArrowFix
{
    sealed class SettingsForm : Form
    {
        public sealed class Bridge
        {
            public Action<bool> SetSource;
            public Action<int> SetMinLevel;
            public Func<List<string>> GetGpus;
            public Func<string> GetSelectedGpu;
            public Action<string> ApplyGpu;   // saves and restarts
            public Func<bool> GetAutostart;
            public Func<bool, string> SetAutostart;   // returns error text or null
        }

        readonly RadioButton rbTemp, rbLoad;
        readonly CheckBox cbMin, cbAuto;
        readonly ComboBox cbGpu;
        readonly Button btnGpu;
        readonly Label lblCpu, lblGpu, lblGpuNote;
        readonly Timer timer;
        readonly Bridge br;
        bool loading;

        public SettingsForm(Icon icon, Bridge bridge)
        {
            br = bridge;
            Text = "HeatSync-ArrowFix Ayarları";
            Icon = icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10f);
            ClientSize = new Size(460, 560);

            int y = 14;
            var title = new Label { Text = "LCD okları neye göre hareket etsin?", Font = new Font("Segoe UI Semibold", 12f), AutoSize = true, Location = new Point(18, y) };
            y += 36;
            rbTemp = new RadioButton { Text = "Sıcaklığa göre (CPU / GPU °C)", AutoSize = true, Location = new Point(24, y) };
            var dTemp = new Label { Text = "0–100 °C ölçeği. Oklar sadece sıcaklık değişince hareket eder.", ForeColor = SystemColors.GrayText, AutoSize = true, Location = new Point(44, y + 24) };
            y += 54;
            rbLoad = new RadioButton { Text = "Kullanım yüzdesine göre (CPU / GPU %)", AutoSize = true, Location = new Point(24, y) };
            var dLoad = new Label { Text = "İşlemci / ekran kartı yükü. Son ~3 sn'nin ortalaması kullanılır.", ForeColor = SystemColors.GrayText, AutoSize = true, Location = new Point(44, y + 24) };
            y += 56;
            cbMin = new CheckBox { Text = "En az 1 ok her zaman yansın", AutoSize = true, Location = new Point(24, y) };
            y += 30;
            cbAuto = new CheckBox { Text = "Windows açılışında otomatik başlat", AutoSize = true, Location = new Point(24, y) };
            y += 40;

            var gpuTitle = new Label { Text = "Ekran kartı (GPU)", Font = new Font("Segoe UI Semibold", 11f), AutoSize = true, Location = new Point(18, y) };
            y += 30;
            cbGpu = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(24, y), Width = 300 };
            btnGpu = new Button { Text = "Uygula", Location = new Point(334, y - 1), Size = new Size(104, 30), Enabled = false };
            lblGpuNote = new Label { Text = "GPU değişince program birkaç saniyede kendini yeniden başlatır.", ForeColor = SystemColors.GrayText, AutoSize = true, Location = new Point(24, y + 32) };
            y += 64;

            var box = new GroupBox { Text = "Şu an LCD'ye giden", Location = new Point(18, y), Size = new Size(424, 92) };
            lblCpu = new Label { AutoSize = true, Location = new Point(14, 28), Font = new Font("Consolas", 10f) };
            lblGpu = new Label { AutoSize = true, Location = new Point(14, 54), Font = new Font("Consolas", 10f) };
            box.Controls.Add(lblCpu); box.Controls.Add(lblGpu);
            y += 102;

            var hint = new Label { Text = "Seçimler anında uygulanır ve kaydedilir; bilgisayar yeniden başlasa da hatırlanır. Bu pencere için HeatSync-ArrowFix.exe'ye tekrar çift tıklayın.", ForeColor = SystemColors.GrayText, AutoSize = true, MaximumSize = new Size(424, 0), Location = new Point(18, y) };
            var ok = new Button { Text = "Kapat", Size = new Size(110, 34), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.OK };
            ok.Location = new Point(ClientSize.Width - ok.Width - 18, ClientSize.Height - ok.Height - 14);
            CancelButton = ok;

            Controls.AddRange(new Control[] { title, rbTemp, dTemp, rbLoad, dLoad, cbMin, cbAuto, gpuTitle, cbGpu, btnGpu, lblGpuNote, box, hint, ok });

            SyncChoice();
            rbTemp.CheckedChanged += (s, e) => { if (!loading && rbTemp.Checked) br.SetSource(false); };
            rbLoad.CheckedChanged += (s, e) => { if (!loading && rbLoad.Checked) br.SetSource(true); };
            cbMin.CheckedChanged += (s, e) => { if (!loading) br.SetMinLevel(cbMin.Checked ? 1 : 0); };
            cbAuto.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                string err = br.SetAutostart(cbAuto.Checked);
                if (err != null)
                {
                    MessageBox.Show(this, "Otomatik başlatma ayarlanamadı:\n" + err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    loading = true; try { cbAuto.Checked = br.GetAutostart(); } catch { cbAuto.Checked = false; } loading = false;
                }
            };

            LoadGpus();
            cbGpu.SelectedIndexChanged += (s, e) => btnGpu.Enabled = cbGpu.SelectedItem != null && (string)cbGpu.SelectedItem != SafeSelected();
            btnGpu.Click += (s, e) =>
            {
                var g = cbGpu.SelectedItem as string;
                if (g == null) return;
                if (MessageBox.Show(this, "Ekran kartı olarak şu seçilecek:\n" + g + "\n\nProgram yeniden başlatılsın mı?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    br.ApplyGpu(g);
            };

            timer = new Timer { Interval = 1000 };
            timer.Tick += (s, e) => RefreshValues();
            timer.Start();
            RefreshValues();
            FormClosed += (s, e) => timer.Dispose();
        }

        string SafeSelected() { try { return br.GetSelectedGpu(); } catch { return null; } }

        void LoadGpus()
        {
            List<string> gpus = null;
            try { gpus = br.GetGpus(); } catch { }
            cbGpu.Items.Clear();
            if (gpus == null || gpus.Count == 0)
            {
                cbGpu.Enabled = false; lblGpuNote.Text = "GPU listesi henüz hazır değil (sensörler yükleniyor).";
                return;
            }
            foreach (var g in gpus) cbGpu.Items.Add(g);
            var sel = SafeSelected();
            if (sel != null && cbGpu.Items.Contains(sel)) cbGpu.SelectedItem = sel;
            cbGpu.Enabled = gpus.Count > 1;
            if (gpus.Count == 1) lblGpuNote.Text = "Bu bilgisayarda tek GPU bulundu.";
        }

        public void SyncChoice()
        {
            loading = true;
            rbTemp.Checked = !HeatSyncFix.Lcd.UseLoad; rbLoad.Checked = HeatSyncFix.Lcd.UseLoad;
            cbMin.Checked = HeatSyncFix.Lcd.MinLevel >= 1;
            try { cbAuto.Checked = br.GetAutostart(); } catch { cbAuto.Checked = false; }
            loading = false;
        }

        static string F(float v, string unit) { return float.IsNaN(v) ? "  -" + unit : ((int)Math.Round(v)).ToString().PadLeft(3) + unit; }
        static string Bar(int lv) { return lv < 0 ? "-" : new string('■', lv) + new string('□', 7 - lv) + " (" + lv + "/7)"; }

        void RefreshValues()
        {
            if (cbGpu.Items.Count == 0) LoadGpus();
            var L = HeatSyncFix.Lcd.LastTemp; var U = HeatSyncFix.Lcd.LastLoad; var K = HeatSyncFix.Lcd.LastLevel;
            lblCpu.Text = "CPU  " + F(L[0], " °C") + "  " + F(U[0], " %") + "   ok " + Bar(K[0]);
            lblGpu.Text = "GPU  " + F(L[1], " °C") + "  " + F(U[1], " %") + "   ok " + Bar(K[1]);
            if (HeatSyncFix.Lcd.LastUpdate == DateTime.MinValue) lblCpu.Text = "LCD bağlantısı bekleniyor…";
        }
    }
}
