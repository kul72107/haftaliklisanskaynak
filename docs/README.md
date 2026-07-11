# Modern Yedek License Data

Bu klasor GitHub Pages uzerinden statik lisans verisi yayinlamak icindir.

Guvenlik kurallari:

- Gercek lisans keylerini buraya duz metin olarak yazma.
- `licenses.txt` ve `licenses.json` icinde sadece SHA256 hash yayinla.
- Aktivasyon bildirimi icin GitHub Pages kullanilmaz; Pages sadece okuma icindir.
- Aktivasyon sinyali icin Google Form, Cloudflare Worker veya kucuk bir API gerekir.

## URL

GitHub Pages `main` branch ve `/docs` klasorunden acilinca dosya URL'leri:

```txt
https://kul72107.github.io/haftaliklisanskaynak/admin/
https://kul72107.github.io/haftaliklisanskaynak/licenses.txt
https://kul72107.github.io/haftaliklisanskaynak/licenses.json
https://kul72107.github.io/haftaliklisanskaynak/revoked.txt
```

`admin/` paneli key uretir, SHA256 hash hesaplar ve `licenses.txt` satiri hazirlar.
GitHub Pages statik oldugu icin panel dosyayi otomatik kaydetmez; uretilen satir GitHub'da
`docs/licenses.txt` dosyasina elle yapistirilir.

## TXT formati

Her lisans satiri:

```txt
SHA256_HASH|durationDays|status|note
```

Ornek:

```txt
F00DBABE00000000000000000000000000000000000000000000000000000000|7|active|example
```

Satir basinda `#` varsa uygulama o satiri yorum kabul eder.

## Key hash uretme

PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\docs\tools\hash-key.ps1 "MY-ORNEK-KEY"
```
