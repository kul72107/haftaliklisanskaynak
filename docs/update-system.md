# Guncelleme Sistemi

Uygulama acilista update manifest dosyasini okur:

```txt
https://raw.githubusercontent.com/kul72107/Yedek-app/main/latest.json
```

Manifest ornegi:

```json
{
  "version": "1.0.0",
  "mandatory": true,
  "url": "https://raw.githubusercontent.com/kul72107/Yedek-app/main/releases/ModernYedek-1.0.0.zip",
  "sha256": "ZIP_SHA256",
  "notes": "Ilk guncelleme paketi.",
  "publishedAt": "2026-07-11T00:00:00Z"
}
```

Akis:

1. Uygulama acilista `latest.json` dosyasini indirir.
2. Manifestteki `version`, uygulamanin kendi surumunden yeniyse update var kabul edilir.
3. `mandatory: true` ise kullanici guncellemeden devam edemez.
4. ZIP indirilir ve SHA256 ile dogrulanir.
5. Uygulama `ModernYedek.Updater.exe` dosyasini temp klasore kopyalar.
6. Ana uygulama kapanir.
7. Updater ZIP'i uygulama klasorune acar.
8. Uygulama yeniden baslatilir.

Notlar:

- Calisan exe kendini degistiremedigi icin ayri updater kullanilir.
- ZIP icinde publish klasorundeki dosyalar dogrudan kokte bulunmalidir.
- Update repo kaynak kod icin degil, sadece `latest.json` ve `releases/*.zip` icindir.
- Yeni patch cikarmak icin uygulama surumu artirilir, publish alinir, ZIP hash hesaplanir ve `latest.json` guncellenir.

