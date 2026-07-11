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

## Manuel lisans sistemi

Bu surum odeme saglayicisina bagli degildir. Keyleri siz uretirsiniz, musterinin uygulamaya girdigi key ilk aktivasyonda o bilgisayara baglanir. `activationLimit=1` oldugunda key baska bilgisayarda kullanilamaz.

Akis:

1. Odeme iyzico, PayTR, Shopier, havale/EFT veya baska bir kanal ile alinir.
2. Odeme onaylaninca telefondan veya tarayicidan License API admin paneli acilir.
3. Email, sure ve cihaz limiti girilerek tek kullanimlik `MY-...` lisans uretilir.
4. Kullanici uygulamadaki `Lisans` ekranina email ve lisans anahtarini girer.
5. Key ilk aktivasyonda bu bilgisayara baglanir ve sure o anda baslar.
6. Uygulama lisansi online dogrular ve sonucu `%APPDATA%\ModernYedek\secrets.dat` icinde DPAPI ile saklar.
7. Internet yoksa son basarili dogrulamadan sonra en fazla 72 saat offline kullanima izin verilir.

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

Node surumu `.NET` API ile ayni endpointleri sunar ve WPF uygulamada ekstra degisiklik gerektirmez. Public URL hazir olunca uygulamadaki `Lisans > License API URL` alanina o base URL yazilir.

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
