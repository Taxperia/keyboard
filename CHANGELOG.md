# Değişiklik günlüğü

Bu projedeki önemli değişiklikler bu dosyada belgelenir.

## [Yayımlanmadı]

### Eklendi

- Eşleşmiş cihazdan gerçek zamanlı birincil ekran görüntüsü ve tam ekran izleyici
- AES-GCM korumalı klavye, fare, pano ve dosya aktarımı
- İzin kontrollü metin panosu eşitleme
- 100 MB'a kadar dosya gönderme ve gelen dosyaları `İndirilenler\KeyBridge Transfers` klasörüne kaydetme
- Gerçek gecikme ölçümü, bildirim geçmişi ve cihaz seçenekleri menüsü
- İlk çalıştırma kurulum ekranı ve genişletilmiş cihaz/izin ayarları
- Cihazlar, Bağlantılar ve Geçmiş için çalışan kenar çubuğu sayfaları
- Font Awesome Free Solid ikonları ve Özel yerel ağ güvenlik duvarı kurulum betiği

### Değiştirildi

- Son Cihazlar'da kayıtlı cihazın kodsuz **Bağlan** akışı netleştirildi; yeni cihaz **Eşleştir**, kimliği değişen cihaz **Doğrula** olarak gösteriliyor
- Yanıt vermeyen bir eşleştirme bağlantısının sonraki onay isteklerini engellemesi önlendi
- Ana pencere laptop ekranlarında daha okunaklı olacak şekilde büyütüldü
- Tam ekran davranışı görev çubuğunun çalışma alanını kapatmayacak şekilde düzenlendi
- Yer tutucu bağlantı kalitesi, dosya aktarımı ve yükseltme davranışları gerçek durumlarla değiştirildi
- Çevrimiçi durumu canlı keşif sinyallerine, bağlantı kalitesi de gerçek ekran oturumuna bağlandı
- Arama ve bağlantı kodu alanlarının yazı başlangıcı daha doğal hizalandı
- Uzak kontrol, alıcı cihazın izni doğrulanmadan yerel girişi kilitlemeyecek şekilde düzeltildi
- Fare ve klavye olayları Windows giriş kancasını bloklamayan bir kuyruğa alındı; fare hareketleri birleştiriliyor
- Klavye ve fare paketleri kayıp ve sıra bozulmasını önleyen kalıcı TCP kontrol oturumuna taşındı
- Kodla ilk bağlantıda seçili görünen Tam Kontrol modunun gerçekten başlatılmaması düzeltildi
- Ekran karesi çözümleme işlemi arayüz iş parçacığından çıkarılarak yerel takılma azaltıldı
- Canlı ekran çözünürlüğü ve JPEG kalitesi artırıldı; önizlemedeki kenar kırpması kaldırıldı
- Bildirim simgesi kaydırılabilir bir dropdown paneline dönüştürüldü
- Arama kutusunun sağ kenarı ve metin başlangıcı, mod kartı yazıları ve büyütülmüş pencere ölçekleri düzenlendi
- Kontrol TCP akışında takılan yazma işlemlerine zaman aşımı eklendi
- Kodla UDP eşleştirmede onay yanıtını tüketen eski dinleme görevleri kaldırıldı; seçilen cihaza TCP ile eşleştirme eklendi
- Gelen oturum, ekran izleyen tarafın durumundan ayrıldı; kabul eden bilgisayardaki yanlış bağlantı/sürüm uyarısı düzeltildi

## [0.2.0] - 2026-09-19

### Eklendi

- Kontrol paneline iki taraflı bağlantı kesme işlemi
- Bağlantı, kontrol yönü, aktif girişler ve paket istatistikleri için yeni durum kartları
- Kompakt, tema uyumlu ayarlar penceresi ve vektör menü ikonları
- GitHub Actions derleme iş akışı ve katkı belgeleri

### Değiştirildi

- Fare aktarımı farklı DPI ve çözünürlüklerde kenarları doğru eşlemek için mutlak sanal ekran koordinatlarını kullanıyor
- Uygulama Per-Monitor V2 DPI farkındalığıyla çalışıyor
- Kontrol paneli ve boş durum ekranları daha anlaşılır hâle getirildi
- Framework-dependent ve self-contained yayın modları ayrı seçeneklerle güvenilir biçimde paketleniyor

## [0.1.0]

- İlk WPF sürümü
- Yerel ağ keşfi, kodla eşleştirme, klavye ve fare aktarımı
