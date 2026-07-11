# Aktivasyon Sinyali

GitHub Pages lisans listesi sadece okunur. Uygulama ilk aktivasyonda lisansi
yerel olarak `secrets.dat` icinde saklar; bu yuzden hash satirini `licenses.txt`
dosyasindan sildikten sonra ayni bilgisayar sure bitene kadar calismaya devam eder.

Aktivasyon oldugunu gorebilmek icin ayrica bir HTTP endpoint gerekir. En kolay
gecici cozum Google Form'dur.

## Google Form alanlari

Formda su kisa cevap alanlarini olustur:

```txt
license_hash
machine_id
computer_name
windows_user
activated_at
expires_at
provider
app_version
```

Formun `formResponse` URL'si ve her alanin `entry.xxxxx` id'si gerekir.

## settings.json ornegi

Uygulama ayarlari bu dosyadadir:

```txt
%APPDATA%\ModernYedek\settings.json
```

Google Form bilgileri geldikten sonra `License` bolumu su sekilde doldurulur:

```json
{
  "License": {
    "Required": true,
    "LicenseListUrl": "https://raw.githubusercontent.com/kul72107/haftaliklisanskaynak/main/docs/licenses.txt",
    "RevokedListUrl": "https://raw.githubusercontent.com/kul72107/haftaliklisanskaynak/main/docs/revoked.txt",
    "ActivationSignalUrl": "https://docs.google.com/forms/d/e/FORM_ID/formResponse",
    "ActivationSignalFields": {
      "license_hash": "entry.111111111",
      "machine_id": "entry.222222222",
      "computer_name": "entry.333333333",
      "windows_user": "entry.444444444",
      "activated_at": "entry.555555555",
      "expires_at": "entry.666666666",
      "provider": "entry.777777777",
      "app_version": "entry.888888888"
    }
  }
}
```

Gercek key Google Form'a gonderilmez. Sadece hash ve cihaz bilgisi gider.

## Akis

1. Panelden key uret.
2. Uretilen hash satirini `docs/licenses.txt` sonuna ekle.
3. Kullanici keyi uygulamaya girer.
4. Uygulama hash listesinden ilk aktivasyonu yapar.
5. Uygulama lisansi bu bilgisayarda sifreli saklar.
6. `ActivationSignalUrl` doluysa Google Form'a sinyal gonderir.
7. Sen Google Sheet'te aktivasyonu gorunce `licenses.txt` satirini silebilirsin.

