# KeyBridge 0.2.5

Bu sürüm, bilgisayarlar arasındaki klavye/fare kontrolünün güvenilirliğini ve uzak ekran akıcılığını iyileştirir.

## Yenilikler

- Daha kompakt, tema uyumlu kontrol paneli ve ayarlar penceresi
- Unicode karakterler yerine ölçeklenebilir vektör ikonlar
- Oturumu kapatma ile iki bilgisayardaki eşleşmeyi kaldırma işlemlerinin ayrılması
- `%100` ve `%125` gibi farklı DPI ölçeklerinde kenar ve köşeleri doğru eşleyen mutlak fare koordinatları
- Per-Monitor V2 DPI farkındalığı
- Fare ve klavye olayları için bloklamayan, sıralı TCP kontrol oturumu
- Kodla bağlantıdan sonra Tam Kontrol modunun otomatik etkinleşmesi
- Son Cihazlar'da kayıtlı cihaz için kodsuz bağlantı ve karşı bilgisayarda açık onay penceresi
- İlk eşleştirme ile kayıtlı cihaza yeniden bağlanmayı ayıran düğme etiketleri
- Font Awesome Free ikonları ve dar/geniş pencerelerde düzeltilmiş arama ve mod kartları
- Özel ağ için TCP/UDP güvenlik duvarı kurulum betiği
- Kodla eşleştirmede onaydan sonra UDP yanıtının kaybolmasına yol açan bekleme hatası giderildi
- Cihaz listesindeki Eşleştir eylemi kodu seçilen bilgisayara doğrudan TCP ile gönderiyor
- Bağlantıyı kabul eden bilgisayarda gelen oturum doğru gösteriliyor; yanıltıcı sürüm uyarısı kaldırıldı
- GitHub Actions, issue şablonları ve katkı belgeleri
- PolyForm Noncommercial 1.0.0 lisansı

## Kurulum

`KeyBridge.exe` taşınabilir uygulamadır. İki bilgisayarda da aynı sürümü çalıştırın ve Windows Güvenlik Duvarı özel ağ iznini kabul edin.

## Gereksinimler

- Windows 10 1809 veya daha yeni bir sürüm
- Aynı yerel ağa bağlı iki bilgisayar

Self-contained EXE için ayrıca .NET kurulumu gerekmez.
