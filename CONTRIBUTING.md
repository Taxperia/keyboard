# KeyBridge'e katkıda bulunma

Katkınız için teşekkürler. Hata düzeltmeleri, erişilebilirlik geliştirmeleri ve ağ güvenilirliği iyileştirmeleri memnuniyetle karşılanır.

## Geliştirme akışı

1. Depoyu fork edin ve değişikliğiniz için kısa isimli bir dal açın.
2. `dotnet build .\KeyBridge.sln -c Release` komutunun hatasız tamamlandığını doğrulayın.
3. Giriş aktarımını etkileyen değişiklikleri mümkünse farklı DPI ölçeklerine sahip iki Windows cihazda deneyin.
4. Kullanıcıya görünen davranış değişikliklerini `CHANGELOG.md` dosyasına ekleyin.
5. Pull request içinde değişikliğin amacını, test adımlarını ve varsa ekran görüntüsünü paylaşın.

## Kod ilkeleri

- Nullable başvuru türlerini koruyun ve yeni derleyici uyarıları eklemeyin.
- Ağdan gelen her paketi cihaz kimliği ve eşleştirme belirteciyle doğrulayın.
- UI işlerini WPF Dispatcher üzerinden çalıştırın.
- Kullanıcı ayarlarının eski sürümlerden okunabilmesini koruyun.
- Yeni dosyaları UTF-8 olarak kaydedin.

## Katkı lisansı

Bir katkı göndererek katkınızı projenin [PolyForm Noncommercial License 1.0.0](LICENSE) koşulları altında yayımlamayı kabul etmiş olursunuz. Ticari kullanım izni verilmez.

## Hata bildirimi

Hata bildiriminde Windows sürümünü, ekran çözünürlüğünü, DPI ölçeğini, iki cihazdaki KeyBridge sürümünü ve tekrar adımlarını ekleyin. Hassas güvenlik sorunları için herkese açık issue açmayın; `SECURITY.md` içindeki yöntemi kullanın.
