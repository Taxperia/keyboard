# KeyBridge Özellik ve Güvenlik Yol Haritası

Bu belge, KeyBridge'e eklenebilecek ürün özelliklerini ve bunların güvenli biçimde uygulanması için gereken teknik çalışmaları listeler. Öncelikler, güvenli varsayılanlar ve saldırı yüzeyini küçük tutma ilkesiyle belirlenmiştir.

## Bu sürümde eklenenler

- Kayıtlı cihazdaki **Bağlan** düğmesi artık kod istemeden, karşı bilgisayarda kullanıcı onayı isteyen şifreli bir oturum isteği gönderir.
- İlk kez 6 haneli kodla bağlantı kurulurken de kodun sahibi bilgisayarda açık Kabul/Reddet onayı istenir.
- **Bağlantıyı Kes** eşleşme anahtarını silmeden yalnızca oturumu kapatır; yeniden bağlantı Son Cihazlar üzerinden yapılır. Kalıcı silme ayrı **Cihazı unut** işlemidir.
- Gelen istek; cihaz adı ve IP adresiyle gösterilir, 30 saniye içinde kabul edilmezse reddedilir.
- Yeniden bağlantı paketi mevcut eşleşme anahtarıyla AES-GCM kullanılarak doğrulanır ve şifrelenir.
- İsteklere zaman damgası ve rastgele nonce eklenir; kısa süreli nonce önbelleği aynı onay paketinin tekrar kullanılmasını reddeder.
- Uygulama açıldığında kayıtlı cihaza otomatik ekran bağlantısı kurulmaz; kullanıcı yeni oturum için açıkça **Bağlan** demelidir.
- Gelen onay verilmeden ekran, klavye/fare, dosya ve pano servisleri eşleşmiş cihazdan gelen trafiği kabul etmez.
- Tam ekran **Uzak Masaüstü** modu, görüntü açıldığında klavye/fare kontrolünü etkinleştirir. Yerel kontrole dönüş kısayolu `Ctrl + Alt + K` olarak gösterilir.
- Dosya aktarımı; yerel klasör tarayıcısı, uzak hedef kuyruğu, durum sütunu ve aktarım günlüğü olan çift panelli bir pencereye taşındı.
- Aynı bilgisayarda ikinci KeyBridge örneğinin ağ portlarını bozması engellendi.

## Önemli ağ sınırı

Mevcut keşif ve doğrudan bağlantı modeli aynı yerel ağ içindir. Sadece altı haneli kod kullanmak, farklı ağlardaki iki bilgisayarın NAT ve güvenlik duvarı arkasından birbirine ulaşmasını sağlamaz. İnternet üzerinden bağlantı için aşağıdaki altyapı kurulmadan uygulama “farklı ağ desteği var” şeklinde sunulmamalıdır:

1. Kimliği doğrulanmış bir sinyal/aracı sunucu.
2. ICE ile bağlantı adaylarının görüşülmesi, STUN ile dış adres keşfi ve doğrudan bağlantı kurulamazsa TURN benzeri aktarım.
3. Sunucunun ekranı veya girdileri okuyamadığı uçtan uca şifreli oturum protokolü.
4. Kod denemelerine hız sınırı, IP/cihaz bazlı geçici kilit ve kötüye kullanım izleme.
5. Sunucu sertifika sabitleme stratejisi, anahtar döndürme ve kesintisiz geri alma planı.

Bu altyapı tamamlanana kadar farklı ağdaki cihaz için kod alanı arayüzde tutulabilir, ancak kullanıcıya bağlantının yalnızca aynı ağda çalıştığı açıkça gösterilmelidir.

## P0 — Yayın öncesi güvenlik gereksinimleri

### Oturum yetkilendirmesi

- Kalıcı eşleşme anahtarını doğrudan her özellikte kullanmak yerine her onaydan sonra kısa ömürlü bir oturum anahtarı türetin.
- Oturum yetkilerini `screen:view`, `input:keyboard`, `input:mouse`, `clipboard:read/write` ve `file:send/receive` kapsamlarına ayırın.
- Karşı taraf reddettiğinde, uygulama kapandığında veya ağ değiştiğinde oturum yetkisini anında iptal edin.
- Ekran, giriş, pano ve dosya servislerinin tamamı aktif oturum kimliği ve kapsam kontrolü yapmadan veri kabul etmemeli.
- Nonce önbelleğini çoklu cihaz ve dağıtık aracı sunucu senaryosunda oturum deposuyla bütünleştirin.

### Cihaz kimliği ve anahtarlar

- Her kurulum için kalıcı Ed25519/X25519 cihaz anahtar çifti üretin.
- Özel anahtarı Windows DPAPI veya Windows Credential Manager ile kullanıcı hesabına bağlı koruyun.
- Mevcut eşleşme belirtecini ayar JSON dosyasında düz metin tutmayın; geçiş sürecinde DPAPI ile koruyup daha sonra cihaz anahtarı tabanlı kimliğe taşıyın.
- İlk eşleşmede cihaz anahtarı parmak izini gösterin ve eşleşme kodunu PAKE benzeri bir protokolün girdisi olarak kullanın.
- Anahtar değiştiğinde sessizce güvenmek yerine kullanıcıya belirgin bir yeniden eşleştirme uyarısı gösterin.

### Ağ ve protokol sağlamlaştırması

- Her paket türüne protokol sürümü, oturum kimliği, sıra numarası, zaman damgası ve azami boyut ekleyin.
- Bağlantılara okuma/yazma zaman aşımı uygulayın; yarım açık istemcilerin dinleyici görevlerini tüketmesini önleyin.
- Eşleştirme, onay ve dosya aktarımında cihaz/IP bazlı hız sınırlama kullanın.
- JSON ayrıştırma hatalarını ve beklenmeyen paketleri sessizce yutmak yerine hassas veri içermeyen güvenlik günlüğüne yazın.
- Windows Güvenlik Duvarı kurallarını yalnızca Özel ağ profiline ve gerekli portlara sınırlandırın.

### Windows giriş güvenliği

- `SendInput` bütünlük seviyesi kısıtını arayüzde açıkça gösterin; normal yetkili uygulama yönetici pencerelerini kontrol edemez.
- Yönetici olarak çalışma isteniyorsa bunun riskini açıklayan ayrı bir onay kullanın.
- UAC güvenli masaüstünü kontrol etmeye çalışmayın; desteklenmediğini belirtin.
- Oturum bittiğinde basılı kalabilecek Ctrl, Alt, Shift ve fare tuşlarını güvenli biçimde serbest bırakın.

## P1 — Uzak masaüstü deneyimi

- Birden fazla monitörü listeleme, monitör değiştirme ve tüm masaüstünü birleştirme.
- Görüntü alanındaki siyah kenarları hesaba katan doğru fare koordinat eşleme.
- Uyarlanabilir FPS, çözünürlük ve JPEG kalitesi; daha sonra donanım hızlandırmalı H.264/AV1 kodlama.
- Gecikme, paket kaybı, çözünürlük ve şifreleme durumunu gösteren bağlantı bilgi paneli.
- `Ctrl + Alt + Del` gibi güvenli dikkat dizilerinin standart kullanıcı modunda desteklenmediğini gösterme.
- Görüntüleme ve kontrol modlarını ayrı tutma; kontrol açıldığında iki tarafta da görünür oturum göstergesi.
- İsteğe bağlı “katılımsız erişim”; varsayılanı kapalı olmalı, ayrı parola/cihaz anahtarı ve süre kısıtı istemeli.

## P1 — FileZilla benzeri dosya yönetimi

Mevcut çift panel güvenli bir gönderim kuyruğudur; uzak dosya sistemini henüz listelemez. Tam uzak dosya yöneticisi eklenirse:

- Karşı kullanıcı her oturumda paylaşılacak kök klasörü seçmeli; bunun dışına çıkış sunucu tarafında engellenmeli.
- `..`, mutlak yol, UNC yolu, sembolik bağ ve NTFS reparse point üzerinden klasör kaçışı engellenmeli.
- Listeleme, indirme, yükleme, yeniden adlandırma ve silme izinleri birbirinden ayrılmalı.
- Silme varsayılan olarak kapalı olmalı ve geri dönüşüm kutusu/ayrı onay kullanmalı.
- Büyük dosyalar belleğe tamamen alınmamalı; parçalı aktarım, devam ettirme, SHA-256 bütünlük doğrulaması ve hız sınırı eklenmeli.
- Eşzamanlı kuyruk, duraklat/devam et, çakışan dosya adında sor/yeniden adlandır/üzerine yaz seçenekleri sunulmalı.
- İndirilen dosyalar için Windows Defender taraması veya Antimalware Scan Interface entegrasyonu değerlendirilmeli.
- Aktarım günlüğü dosya içeriğini, eşleşme anahtarını veya tam kullanıcı yollarını uzaktaki tarafa sızdırmamalı.

## P2 — Kullanışlı özellikler

- Salt metin dışında isteğe bağlı dosya ve görsel pano aktarımı.
- Oturum daveti geçmişi, kabul/ret zamanı ve cihaz parmak izi içeren yerel denetim günlüğü.
- Favori cihazlar, cihaz takma adı ve güvenilen cihazı unutma.
- Bağlantı kalitesi değiştiğinde otomatik görüntü kalitesi ayarı.
- Dosya sürükle-bırak, masaüstüne bırakma ve aktarım bildirimi.
- Ekran kaydı ve ekran görüntüsü alma; karşı tarafa görünür bildirim ve açık izinle.
- Uygulama içi güncelleme denetimi, imzalı paket doğrulaması ve güvenli geri alma.

## Yayın ve tedarik zinciri güvenliği

- EXE ve kurulum paketlerini EV/standart kod imzalama sertifikasıyla imzalayın.
- GitHub Actions bağımlılıklarını commit SHA ile sabitleyin ve minimum iş akışı izinleri kullanın.
- NuGet bağımlılık kilit dosyası, SBOM ve yayın başına SHA-256 sağlama toplamı üretin.
- Gizli anahtarları depoya veya loglara yazmayın; yayın imzasını ayrı, denetlenebilir ortamda üretin.
- Otomatik güncelleme manifestini imzalayın ve eski/savunmasız sürüme düşürme saldırısını engelleyin.

## Test planı

- İki gerçek Windows cihazında kabul, ret, zaman aşımı, uygulama kapanması ve IP değişimi senaryoları.
- Genel/Özel ağ profilleri ve Windows Güvenlik Duvarı açıkken port doğrulaması.
- Farklı DPI, çözünürlük, çoklu monitör ve negatif sanal masaüstü koordinatları.
- Büyük/bozuk paket, yanlış anahtar, tekrar kullanılan nonce ve çok hızlı kod denemeleri için negatif testler.
- Dosya adı yol geçişi, reparse point, sıfır bayt, azami boyut ve aktarım kesintisinden devam testleri.
- Normal kullanıcı/yönetici süreçleri arasındaki giriş enjeksiyonu davranışı.
