# Modern Yedek

Modern Yedek, eski `yedek.exe` paketinin yaptigi temel isi yeni bir .NET 8 WPF uygulamasi olarak uygular. Eski `yedekaldat.ini` ayarlarini ice aktarabilir, ZIP yedek olusturur, ZIP'i dogrular, SHA256 hash uretir, rotasyon uygular, log tutar ve Google Cloud Storage bucket'ina yukleme yapabilir.

## Calistirma

Publish edilen uygulama:

```powershell
D:\BITCH\ModernYedek\publish\ModernYedek\ModernYedek.App.exe
```

Derleyerek calistirmak icin:

```powershell
dotnet run --project D:\BITCH\ModernYedek\src\ModernYedek.App\ModernYedek.App.csproj
```

## Installer

Son kullanici setup dosyasi `installer/` altindaki bootstrap kaynaklarindan uretilir. Setup uygulamayi icine gommez; `latest.json` uzerinden en guncel release ZIP paketini indirir ve SHA256 ile dogrulayarak kurar.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\BITCH\ModernYedek\installer\build-installer.ps1
```

## Ayarlar

Uygulama ilk acilista `D:\yedek_kopya\yedekaldat.ini` dosyasini bulursa ayarlari otomatik ice aktarir. Sonraki ayarlar kullanici profilinde tutulur:

- Genel ayarlar: `%APPDATA%\ModernYedek\settings.json`
- Sifreli secret dosyasi: `%APPDATA%\ModernYedek\secrets.dat`
- Loglar: `%APPDATA%\ModernYedek\logs.jsonl`

`secrets.dat`, Windows DPAPI CurrentUser kapsami ile sifrelenir. Mail parolasi, ZIP parola notu ve Google service account JSON icerigi bu dosyada duz metin tutulmaz.

## Google Cloud Storage

Bulut sayfasinda su bilgiler girilir:

- Bucket adi
- Klasor prefix
- Service Account JSON
- Yedekten sonra buluta yukle
- Basarili yuklemeden sonra yerel ZIP'i sil

Service account icin bucket uzerinde yazma yetkisi gerekir. Uygulama Google hesabi veya bucket satin alma islemini yapmaz; kullanici kendi Google Cloud hesabinda bucket olusturur.

## Dogrulama

```powershell
dotnet build D:\BITCH\ModernYedek\ModernYedek.sln
dotnet run --project D:\BITCH\ModernYedek\tests\ModernYedek.Tests\ModernYedek.Tests.csproj
```

## Guncelleme

Uygulama acilista update manifest dosyasini kontrol eder. Zorunlu update varsa
ZIP paketini indirir, SHA256 ile dogrular ve `ModernYedek.Updater.exe` ile
kendini gunceller. Detaylar: `docs/update-system.md`.

## Manuel lisans sistemi

Bu surum odeme saglayicisina bagli degildir. Keyleri siz uretirsiniz, musterinin uygulamaya girdigi key ilk aktivasyonda o bilgisayara baglanir. `activationLimit=1` oldugunda key baska bilgisayarda kullanilamaz.

Akis:

1. Odeme iyzico, PayTR, Shopier, havale/EFT veya baska bir kanal ile alinir.
2. Odeme onaylaninca GitHub Pages lisans paneli acilir.
3. Sure ve not girilerek tek kullanimlik `MY-...` lisans uretilir.
4. Kullanici uygulamadaki `Lisans` ekranina sadece lisans anahtarini girer.
5. Uygulama `docs/licenses.txt` listesinden hash kontrolu yapar.
6. Key ilk aktivasyonda bu bilgisayara baglanir ve sure o anda baslar.
7. Uygulama sonucu `%APPDATA%\ModernYedek\secrets.dat` icinde DPAPI ile saklar.
8. Aktivasyon sinyali ayarlandiysa uygulama Google Form gibi bir endpoint'e hash ve cihaz bilgisini gonderir.
9. Aktivasyonu gordukten sonra hash satiri `licenses.txt` dosyasindan silinebilir; aktif cihaz sure bitene kadar calismaya devam eder.

Lisans iptali icin hash degeri `docs/revoked.txt` dosyasina eklenir. Uygulama
acilista ve yedekleme oncesi iptal listesini kontrol eder. Son basarili iptal
kontrolu 24 saati gectiyse yedekleme internet baglantisi gelene kadar kilitlenir.

License API calistirma:

```powershell
$env:MODERN_YEDEK_ADMIN_TOKEN="degistirilecek-admin-token"
$env:MODERN_YEDEK_LICENSE_SALT="degistirilecek-lisans-salt"
dotnet run --project D:\BITCH\ModernYedek\src\ModernYedek.LicenseApi\ModernYedek.LicenseApi.csproj
```

Node.js ile deploy edilecek alternatif API:

```bash
cd node-license-api
MODERN_YEDEK_ADMIN_TOKEN="degistirilecek-admin-token" \
MODERN_YEDEK_LICENSE_SALT="degistirilecek-lisans-salt" \
MODERN_YEDEK_LICENSE_DB="./data/licenses.json" \
PORT="5088" \
npm start
```

Node surumu `.NET` API ile ayni endpointleri sunar. Public URL uygulama icine varsayilan lisans sunucusu olarak gomuludur; kullanici sadece key girer.

Telefondan key uretme paneli:

```text
http://SUNUCU_ADRESI:5088/admin/keygen
```

Panelde `MODERN_YEDEK_ADMIN_TOKEN`, musteri emaili, sure ve cihaz limiti girilir.

Komutla lisans uretme:

```powershell
Invoke-RestMethod -Method Post `
  -Uri http://localhost:5088/admin/licenses `
  -Headers @{ "X-Admin-Token" = "degistirilecek-admin-token" } `
  -ContentType "application/json" `
  -Body '{"email":"musteri@example.com","days":7,"activationLimit":1,"plan":"weekly_pro","startsOnActivation":true}'
```

Lisans uzatma:

```powershell
Invoke-RestMethod -Method Post `
  -Uri http://localhost:5088/admin/licenses/MY-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX/extend `
  -Headers @{ "X-Admin-Token" = "degistirilecek-admin-token" } `
  -ContentType "application/json" `
  -Body '{"days":7}'
```
