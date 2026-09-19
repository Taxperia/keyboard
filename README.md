# KeyBridge

KeyBridge, aynı yerel ağdaki iki Windows bilgisayarı tek klavye ve fareyle kontrol etmeyi sağlayan bir WPF uygulamasıdır.

> **Lisans:** Kaynak kod kişisel ve diğer ticari olmayan kullanımlar için paylaşılır. Ticari kullanım yasaktır.

## Özellikler

- 6 haneli kodla kolay cihaz eşleştirme
- `Ctrl + Alt + K` ile yerel ve uzak bilgisayar arasında hızlı geçiş
- Klavye, fare hareketi, tıklama ve kaydırma aktarımı
- Farklı çözünürlük ve DPI ölçeklerinde ekranın tüm kenarlarına ulaşan mutlak fare eşleme
- Tek işlemle iki cihazdaki eşleşmeyi kaldıran bağlantı kesme akışı
- Açık, koyu ve mor tema seçenekleri
- Yerel ağda otomatik cihaz keşfi
- Taşınabilir tek dosya ve Inno Setup kurulum paketi üretimi

## Gereksinimler

- Windows 10 1809 veya daha yeni bir Windows sürümü
- Geliştirme için [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- İki cihazın aynı yerel ağda olması

Self-contained yayın paketini kullanan son kullanıcıların ayrıca .NET kurmasına gerek yoktur.

## Hızlı başlangıç

1. KeyBridge'i iki bilgisayarda da açın.
2. İlk kurulum ekranında paylaşılacak girişleri ve cihaz rolünü seçin.
3. Cihazlardan birinde eşleştirme kodu üretin.
4. Diğer cihazda 6 haneli kodu girin.
5. Kontrol panelindeki **Diğer bilgisayara geç** düğmesini veya `Ctrl + Alt + K` kısayolunu kullanın.
6. Eşleşmeyi kaldırmak için kontrol panelindeki **Bağlantıyı kes** düğmesini kullanın.

Windows Güvenlik Duvarı ilk çalıştırmada ağ erişimi isteyebilir. Yerel ağ keşfi ve giriş aktarımı için özel ağ erişimine izin verin.

## Geliştirme

```powershell
git clone https://github.com/Taxperia/keyboard.git
cd keyboard
dotnet restore .\KeyBridge.sln
dotnet build .\KeyBridge.sln
dotnet run --project .\KeyBridge\KeyBridge.csproj
```

### Proje yapısı

```text
KeyBridge/                  WPF uygulaması ve servisler
  Models/                   Ağ üzerinden taşınan modeller ve ayarlar
  Services/                 Keşif, eşleştirme ve giriş aktarımı
  Services/WindowsInput/    Windows hook ve SendInput katmanı
installer/                  Inno Setup tanımı
scripts/                    Yayın ve kurulum betikleri
.github/workflows/          GitHub Actions derleme doğrulaması
```

## Yayınlama

Tek dosyalı, self-contained Windows x64 paketi oluşturmak için:

```powershell
.\scripts\publish-release.ps1
```

Çıktı `artifacts\publish\KeyBridge-win-x64\KeyBridge.exe` altında oluşur. Daha küçük, .NET Desktop Runtime gerektiren paket için:

```powershell
.\scripts\publish-release.ps1 -Mode framework-dependent
```

Inno Setup kuruluysa kurulum dosyası üretmek için:

```powershell
.\scripts\make-installer.ps1
```

Kurulum çıktısı `artifacts\installer\KeyBridgeSetup.exe` olur.

## Ağ ve güvenlik

KeyBridge yalnızca güvenilen yerel ağlarda kullanılmak üzere tasarlanmıştır. Keşif ve eşleştirme için UDP, giriş paketleri için TCP (UDP yedekli) kullanır. Eşleşen cihazlar rastgele bir eşleştirme belirteciyle doğrulanır; trafik uçtan uca şifrelenmez. KeyBridge portlarını internete yönlendirmeyin.

Varsayılan portlar:

- Keşif: UDP `48740`
- Eşleştirme: TCP/UDP `48741`
- Klavye ve fare aktarımı: TCP/UDP `48742`

Güvenlik açığı bildirmek için [SECURITY.md](SECURITY.md) dosyasını inceleyin.

## Katkıda bulunma

Katkı adımları ve kod standartları [CONTRIBUTING.md](CONTRIBUTING.md) içinde yer alır. Önemli değişiklikler [CHANGELOG.md](CHANGELOG.md) dosyasında izlenir.

## Lisans

KeyBridge, [PolyForm Noncommercial License 1.0.0](LICENSE) ile lisanslanır. Kişisel, eğitimsel, araştırma ve diğer ticari olmayan kullanımlara lisans koşulları kapsamında izin verilir. Ticari kullanım, ticari ürüne veya hizmete dahil etme ve ticari amaçla dağıtım için ayrıca yazılı izin gerekir.

Telif bildirimi: Copyright 2026 Taxperia. Ayrıntılar için [NOTICE](NOTICE) dosyasına bakın.

## Bilinen sınırlamalar

- Yönetici yetkisiyle çalışan bir pencereye giriş göndermek için KeyBridge'in de aynı yetki seviyesinde çalıştırılması gerekebilir.
- Mevcut ağ aktarımı şifreli değildir; uygulamayı yalnızca güvendiğiniz ağlarda kullanın.
- macOS ve Linux desteklenmez.
