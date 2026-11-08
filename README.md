<p align="center">
  <img src="docs/icon.png" width="96" alt="HeatSync-ArrowFix">
</p>

<h1 align="center">HeatSync-ArrowFix</h1>

<p align="center">
  <b>GAMEPOWER HeatSync A-RGB LCD VGA Holder</b> yazılımı için düzeltme paketi.<br>
  LCD'deki CPU/GPU okları artık durmadan akmıyor, gerçek sıcaklığa göre duruyor.
</p>

<p align="center">
  <a href="../../releases/latest"><img alt="İndir" src="https://img.shields.io/github/v/release/mcozcan/Gamepower-HeatSync-ArrowFix?label=%C4%B0ndir&style=for-the-badge&color=2563eb"></a>
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?style=for-the-badge&logo=windows">
  <a href="LICENSE"><img alt="MIT" src="https://img.shields.io/badge/Lisans-MIT-16a34a?style=for-the-badge"></a>
</p>

<p align="center">
  <a href="https://mcozcan.github.io/Gamepower-HeatSync-ArrowFix/">Tanıtım sayfası</a> ·
  <a href="docs/TEKNIK.md">Teknik inceleme</a> ·
  <a href="../../issues">Sorun bildir</a>
</p>

---

## Neyi düzeltir?

| Sorun (orijinal yazılım 2.2.1.0) | Sebep | HeatSync-ArrowFix |
|---|---|---|
| LCD'deki renkli oklar sıcaklık sabitken bile **sürekli akıyor** | Yazılım ok değerine sıcaklık yerine 0→7 arasında **sürekli dönen bir sayaç** gönderiyor; göstergenin bir kısmına da CPU *yükü* gidiyor | Sol ok **CPU sıcaklığına**, sağ ok **GPU sıcaklığına** göre sabit durur; sadece sıcaklık değişince hareket eder |
| Açılışta *"Uygulamanızda işlenmemiş özel durum oluştu. Nesne başvurusu bir nesnenin örneğine ayarlanmadı"* | LCD'nin seri portu henüz açılmadan ona yazılmaya çalışılıyor (port `null`) | Port hazır değilse "cihaz henüz bağlı değil" olarak işlenir, yeniden bağlanır, hata penceresi çıkmaz |
| Gizli pencereye Enter/Boşluk gidince *"Parametre geçerli değil"* | Üreticinin unuttuğu gizli bir geliştirici butonu, olmayan `D:\ch554\...` dosyasını açmaya çalışıyor | Buton devre dışı |
| Çok monitörlü bilgisayarda köşede Çince **"设置"** butonlu küçük bir pencere görünüyor | Yazılım ana penceresini (-500,-500) noktasına taşıyarak "gizliyor"; birincil ekranın solunda/üstünde monitör varsa bu nokta görünür | Pencere tüm ekranların dışına (-32000,-32000) taşınır |
| Program **aniden kapanıyor** (Olay Günlüğü: `AccessViolationException`, `nvml.dll`) | HWiNFO sensör kütüphanesi NVIDIA sürücüsünde erişim ihlaline düşüyor | Çağrı korunuyor: hata olursa son değerler korunur, program kapanmaz |

CPU sıcaklığı HWiNFO'nun **CPU (Tctl/Tdie)** sensöründen, GPU sıcaklığı **GPU Temperature** sensöründen okunur. CPU ekranın solunda, GPU sağında gösterilir.

## İndirme ve kurulum

> **Gerekli:** Üreticinin **GAMEPOWER HeatSync** yazılımı (sürüm **2.2.1.0**) bilgisayarınızda kurulu olmalı.
> HeatSync-ArrowFix üreticinin dosyalarını **içermez**; ilk açılışta sizin kurulu kopyanızdan alır.

1. [**Releases**](../../releases/latest) sayfasından `HeatSync-ArrowFix-vX.Y.Z.zip` dosyasını indirin.
2. ZIP'i ayrı bir klasöre çıkarın (ör. `Belgeler\HeatSync-ArrowFix`). Kurulu GAMEPOWER klasörünün **içine çıkarmayın**.
3. **`HeatSync-ArrowFix.exe`** dosyasına çift tıklayın ve yönetici iznini onaylayın (orijinal yazılım da bunu ister).
4. İlk açılışta program, kurulu GAMEPOWER'ı (`C:\Program Files (x86)\GAMEPOWER\...`) bulur, sürümünü SHA-256 ile doğrular ve onayınızla düzeltilmiş kopyayı kendi klasöründe oluşturur.
5. Windows açılışında otomatik başlamasını isterseniz aşağıdaki [Windows açılışında başlatma](#windows-açılışında-başlatma) adımlarını uygulayın.

Program pencere açmaz; saatin yanındaki **sistem tepsisinde** çalışır.

**Orijinal kurulum hiçbir şekilde değiştirilmez.** Kaldırmak için klasörü silmeniz yeterli.

## Windows açılışında başlatma

HeatSync-ArrowFix Windows'un açılış ayarlarına **kendiliğinden dokunmaz**. Otomatik başlamasını isterseniz bir kez elle ayarlayın:

1. Başlat menüsünde **Görev Zamanlayıcı**'yı açın → sağda **Görev Oluştur…** (Temel görev değil).
2. **Genel:** Ad `HeatSync-ArrowFix`, **En yüksek ayrıcalıklarla çalıştır** işaretli.
3. **Tetikleyiciler → Yeni:** *Görevi başlat:* **Oturum açıldığında**.
4. **Eylemler → Yeni:** *Program:* `HeatSync-ArrowFix.exe` dosyasının tam yolu · *Bağımsız değişkenler:* `-auto` · *Başlangıç konumu:* bu klasörün yolu.
5. **Koşullar:** Dizüstü bilgisayardaysanız *"Yalnızca bilgisayar AC gücündeyse başlat"* işaretini kaldırın → **Tamam**.

Orijinal yazılımın açılış görevi (`UsbMonitor`) duruyorsa onu **Devre dışı bırakın**; aksi hâlde iki program LCD için çakışır.

## Windows Defender uyarısı

İmzasız, yeni ve yönetici izni isteyen programlar Defender'ın yapay zekâ tabanlı tahmininde bazen `Trojan:Win32/Wacatac.B!ml` olarak işaretlenir. Sondaki **`!ml`**, bunun bilinen bir virüs imzası değil, otomatik bir **şüphe tahmini** olduğunu gösterir.

- **1.0.0**'daki başlatıcı açılış görevini kendisi yazıyor ve diğer programı zorla kapatıyordu; Defender bunu işaretledi. **1.0.1**'de bu iki davranış tamamen kaldırıldı.
- Kaynak kodun tamamı bu depoda; paketi `build.ps1` ile kendiniz derleyebilirsiniz.
- İndirdiğiniz ZIP'in SHA-256 değerini [Releases](../../releases/latest) sayfasındaki değerle karşılaştırabilirsiniz (`Get-FileHash dosya.zip`).
- Yanlış alarm görürseniz Microsoft'a bildirebilirsiniz: <https://www.microsoft.com/wdsi/filesubmission>

## Ayar penceresi

İlk açılışta, ya da program çalışırken `HeatSync-ArrowFix.exe`'ye **tekrar çift tıklayınca**, **HeatSync-ArrowFix Ayarları** penceresi açılır. Saatin yanındaki HeatSync-ArrowFix simgesine tıklamak da aynı pencereyi açar.

- **LCD okları neye göre hareket etsin?**
  - **Sıcaklığa göre (CPU / GPU °C)**: varsayılan. 0–100 °C ölçeği; oklar yalnızca sıcaklık değişince hareket eder.
  - **Kullanım yüzdesine göre (CPU / GPU %)**: işlemci/ekran kartı yükü. Son ~3 saniyenin ortalaması kullanılır; oklar her saniye zıplamaz.
- **En az 1 ok her zaman yansın**: varsayılan olarak açık.
- **Ekran kartı (GPU)**: İşlemcide dahili grafik varsa (ör. AMD Radeon) ve ayrıca harici kart takılıysa (ör. NVIDIA), LCD'nin sağ tarafında hangisinin gösterileceğini seçin. **Uygula** dediğinizde program birkaç saniyede kendini yeniden başlatır.
- **Şu an LCD'ye giden**: CPU/GPU sıcaklığı, kullanımı ve yanan ok sayısı canlı görünür.

Seçimler anında uygulanır ve `HeatSync-ArrowFix.ini`'ye kaydedilir. Sol ok her zaman CPU, sağ ok GPU içindir. Programı kapatmak için simgeye sağ tıklayıp **Çıkış** deyin.
## Ayarlar

`HeatSync-ArrowFix.ini`:

```ini
; Oklar neye göre: Temp (sıcaklık) | Load (kullanım %) - tepsi menüsünden de değişir
ArrowSource=Temp
; Okların sıcaklık ölçeği (varsayılan 0..100 °C, 8 kademe = 12,5 °C)
ArrowMinC=0
ArrowMaxC=100
; Kademe sınırında titremeyi önleyen pay (°C)
HysteresisC=1
; En az kaç ok yansın (0..7)
MinLevel=1
; Kullanım modunda ortalama süresi (sn) ve titreme payı (yüzde puan)
LoadSmoothingSec=3
LoadHysteresis=3
```

Okların daha belirgin hareket etmesi için ör. `ArrowMinC=30`, `ArrowMaxC=90` kullanabilirsiniz.

| Komut | Ne yapar |
|---|---|
| `HeatSync-ArrowFix.exe --debug` | Ayrıntılı kayıt: LCD'ye giden her paket `logs\lcd_*.log`'a yazılır |
| `HeatSync-ArrowFix.exe --lcd-test` | Okları 0→7 değerlerinde 5'er saniye gösterir |
| `HeatSync-ArrowFix.exe --setup` | Düzeltilmiş kopyayı kurulu orijinalden yeniden oluşturur |

## Sık sorulanlar

**Okların rengini değiştirebilir miyim?**
Hayır. Renkler LCD'nin kendi belleğindeki görüntülerde. Yazılım sadece kaç ok yanacağını gönderebiliyor (`--lcd-test` ile görebilirsiniz). Tutucunun gövdesindeki büyük ARGB ışığın rengi ise anakartınızın RGB yazılımından ayarlanır.

**Orijinal program açıkken çalışır mı?**
LCD'yi (COM portunu) iki program aynı anda kullanamaz. HeatSync-ArrowFix bunu algılar ve diğerini sistem tepsisinden kapatmanızı ister; hiçbir programı kendisi zorla kapatmaz.

**Başka bir sürümle çalışır mı?**
Şimdilik yalnızca 2.2.1.0 (SHA-256 `994498FE…5E12`). Farklı sürüm algılanırsa hiçbir şey değiştirilmez. Lütfen bir [issue](../../issues) açın.

## Kaynaktan derleme

```powershell
# .NET SDK 6+ gerekir (net48 hedefleri referans paketleriyle derlenir)
./build.ps1 -Version 1.0.1
# çıktı: artifacts/HeatSync-ArrowFix-v1.0.1.zip
```

| Klasör | İçerik |
|---|---|
| `src/HeatSyncArrowFix` | Başlatıcı + `CorePatcher` (dnlib ile kullanıcının kendi kopyasına IL yaması) |
| `src/HeatSyncFix` | Yamalı yazılımın çağırdığı yardımcı kod (ok kademesi, sensör koruması, protokol kaydı) |
| `docs` | Tanıtım sayfası ve [teknik inceleme](docs/TEKNIK.md) |

## Yasal

Bu proje **GAMEPOWER**, **TURZX** veya **REALiX (HWiNFO)** ile bağlantılı değildir. Bu depo ve sürümler üreticilere ait hiçbir dosyayı içermez. Düzeltmeler, kullanıcının kendi bilgisayarında, kendi lisanslı kurulumunun bir kopyasına uygulanır. Yazılım "olduğu gibi" sunulur, kullanım riski size aittir. Firmware'e veya cihaz belleğine hiçbir şey yazılmaz.

Bu deponun kodu [MIT](LICENSE) lisanslıdır. Üçüncü taraf bileşenler için [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) dosyasına bakın.

---

<sub><b>English:</b> Unofficial fix for the GAMEPOWER HeatSync A-RGB LCD VGA holder software 2.2.1.0. The LCD arrows follow the real CPU/GPU temperature instead of an endless animation counter. It also fixes the startup NullReferenceException and guards against the HWiNFO/nvml crash. It patches a copy of your own installation locally and redistributes no vendor files.</sub>
