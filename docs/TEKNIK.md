# Teknik inceleme

Bu sayfa, sorunların kaynağının nasıl bulunduğunu ve neyin değiştirildiğini anlatır.
İnceleme GAMEPOWER HeatSync yazılımı **2.2.1.0** (`GAMEPOWER.exe`, SHA-256 `994498FE073B038E34D1318E577042B079823412BBCF5C2AC810E0CD03185E12`) üzerinde yapıldı.

## Yazılımın yapısı

| Dosya | Gerçekte ne |
|---|---|
| `GAMEPOWER.exe` | .NET Framework 4.8 WinForms uygulaması. Derleme adı `usb_hid`, **Dotfuscator** ile karıştırılmış (isim gizleme, kontrol akışı karıştırma, XOR string şifreleme). TURZX ekran yazılımının OEM sürümü. Kodda `ℽ._20ED = 11` sabit; GAMEPOWER varyantı. |
| `msvcr120d.dll` | Microsoft CRT **değil**: adı değiştirilmiş **HWiNFO64 istemci kütüphanesi** (REALiX imzalı, 8.21). Fonksiyonları sıra numarasıyla (`#216`, `#566`…) çağrılıyor. Sensörlerin hepsi buradan okunuyor; yönetici izni bu yüzden gerekiyor. |
| `config\*.data` | .NET `BinaryFormatter` ile serileştirilmiş `AppConfig` / `ThemeConfig` nesneleri. |
| LCD | `USB\VID_1A86&PID_5722\USB35INCHIPSV2`. Bir **seri port** (COM, 115200 baud) üzerinden konuşuyor, HID değil. |

## LCD protokolü (bu üründe)

Bu üründe ekranı PC çizmiyor. Uygulama yaklaşık 0,65 sn'de bir **20 baytlık bir paket** gönderiyor, rakamları ve okları LCD'nin kendi yazılımı çiziyor.

Paket, TURZX "rev A" komut biçiminde. İlk 6 bayt 4 adet 10 bitlik alan ve bir komut baytından oluşuyor, komut **169 (0xA9)**:

| Konum | Orijinal yazılım | HeatSync-ArrowFix |
|---|---|---|
| başlık alan 1 | `(byte)` CPU **yükü** | CPU sıcaklığı |
| başlık alan 2 | `(byte)` GPU sıcaklığı | GPU sıcaklığı |
| başlık alan 3 | `(byte)` CPU **yükü** | CPU sıcaklığı |
| başlık alan 4 | 0 | 0 |
| `[6]`,`[7]` | CPU sıcaklığı onlar / birler | (aynı) |
| `[8]`,`[9]` | GPU sıcaklığı onlar / birler | (aynı) |
| `[10]` | **0→7 dönen sayaç** | CPU sıcaklığına göre ok kademesi 0..7 |
| `[11]` | **0→7 dönen sayaç** | GPU sıcaklığına göre ok kademesi 0..7 |

Kaynak: `x/ℸ::⃼` (GAMEPOWER varyantının LCD döngüsü). Orijinal kod, karıştırma çözüldükten sonra şöyle:

```csharp
ℋ[6]  = (byte)(cpuTemp % 100f / 10f);  ℋ[7] = (byte)(cpuTemp % 10f);
ℋ[8]  = (byte)(gpuTemp % 100f / 10f);  ℋ[9] = (byte)(gpuTemp % 10f);
ℋ[10] = (byte)counter;                 ℋ[11] = (byte)counter;      // <- sıcaklıktan bağımsız
lcd.Send(169, (byte)cpuLoad, (byte)gpuTemp, (byte)cpuLoad, 0, ℋ);
counter++; if (counter > 7) counter = 0;
```

`[10]`/`[11]` değerlerinin ekrandaki karşılığı cihaz üzerinde denendi: değer arttıkça **yanan ok sayısı/konumu** değişiyor, renk değişmiyor.

Kademe hesabı (`HeatSyncFix.Lcd.ArrowLevel`): 0–100 °C aralığı 8 eşit kademeye bölünüyor. Bu, orijinal yazılımın sıcaklık çubuklarında kullandığı 0–100 ölçeğiyle aynı. Kademe sınırında titremeyi önlemek için 1 °C pay bırakılıyor.

Ölçüm (gerçek cihaz, 90 sn, `--debug`): eskiden her pakette değişen `[10]/[11]` artık yalnızca sıcaklık bir kademe sınırını geçince değişiyor. GPU rakamları `nvidia-smi` ile karşılaştırıldı, fark en fazla 1 °C (tam sayıya yuvarlama).

> `ThemeConfig` (ör. `NZXT_BLUR.data`) bu üründe LCD'ye **çizilmiyor**. Oradaki `StatuBar.stepWidth` değerlerini değiştirmek bu yüzden işe yaramaz. Üstelik kod, açılışta etkin çubuklar için bu değeri her zaman 1'e geri alıyor.

## "Nesne başvurusu bir nesnenin örneğine ayarlanmadı"

Süreç içinde alınan stack trace:

```
System.NullReferenceException
   at ⃷.ℚ(Byte[] A_0, Boolean A_1)                         // seri porta yazma
   at ℐ.ℚ(Int32, Int32, Int32, Int32, Int32, Byte[], Int32) // LCD komutu
   at ℐ.⃷.⃼()                                               // "test connect"
```

`⃷.ℚ(byte[], bool)` metodu `SerialPort.Write` çağırıyor, ama port henüz bulunup açılmadıysa nesne `null`; kodda bunu kontrol eden bir satır yok. Bağlantı testi bu durumu kendi `try/catch`'iyle yakalıyor. Ancak `async void` buton işlemleri de (ör. Çalıştır: `x._21E2` → bağlantı testi → cihaza yazma) aynı yola giriyor. Orada istisna arayüz iş parçacığına çıkıyor ve WinForms'un "işlenmemiş özel durum" penceresi açılıyor.

**Düzeltme:** Yazmadan önce port `null` veya kapalıysa `IOException("LCD serial port is not open…")` fırlatılıyor. Bağlantı testi bunu "bağlı değil" olarak işleyip yeniden bağlanıyor. Başlatıcı da bu istisnayı pencere göstermeden kaydediyor.

## Ani kapanma (AccessViolationException)

Windows Olay Günlüğü (.NET Runtime 1026, birden fazla kez):

```
System.AccessViolationException
   at ⅌.⃲(Int32)        // [DllImport("msvcr120d.dll", EntryPoint="#216")] HWiNFO: aygıt sensörlerini güncelle
   at ⅌+⃲.⃷()           // sensör yoklama döngüsü
Hatalı modül: nvml.dll
```

**Düzeltme:** Beş çağrı noktası da `HeatSyncFix.Sensors.SafeUpdate` üzerinden geçiyor (`[HandleProcessCorruptedStateExceptions]`). Erişim ihlali olursa o aygıt 5 sn atlanıyor, son değerler korunuyor ve olay kaydediliyor. Hatanın kendisi sürücüde olduğu için engellenemiyor, ama artık programı kapatmıyor. Bu mekanizma gerçek bir yerel erişim ihlaliyle test edildi.

## Gizli geliştirici butonu

Gizli ana pencerede `TabIndex = 0` olan `button1` ("新主题", yeni tema) varsayılan odakta. Enter veya Boşluk tuşu onu tetikliyor ve geliştiricinin bilgisayarındaki `D:\ch554\2.4osd\osd_full.png` yüklenmeye çalışılıyor (`ArgumentException: Parametre geçerli değil`). İşleyici boş bırakıldı.

## Çok monitörde görünen pencere

Ana form, yükleme sırasında `base.DesktopLocation = new Point(-500, -500)` ile "gizleniyor" (300×200 px, ortasında "设置" = Ayarlar butonu). Birincil ekranın solunda/üstünde başka bir monitör varsa (ör. `X=-1080, Y=-474` konumunda dikey bir ekran) bu nokta görünür alanda kalıyor. Konum `(-32000, -32000)` yapıldı; Ayarlar açılırken yazılım pencereyi zaten `CenterToScreen()` ile ortalıyor.

## Yöntem

- Kod ILSpy ile C#'a geri çevrildi; şifreli string'ler (`s.b(str, key)`, XOR) bir araçla çözüldü.
- Uygulama, yönetici olarak, içine istisna/Console kaydı eklenmiş bir ana süreçte çalıştırıldı.
- Seri port yazma metoduna bir kanca eklenerek LCD'ye giden her bayt kaydedildi.
- Yamalar [dnlib](https://github.com/0xd4d/dnlib) ile IL düzeyinde uygulanıyor (`src/HeatSyncArrowFix/CorePatcher.cs`). Yamalı derlemedeki 963 metodun hepsi JIT ile derlenerek doğrulandı.
