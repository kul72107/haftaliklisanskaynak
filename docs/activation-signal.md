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
activated_at
app_version
note
```

Istege bagli olarak `windows_user`, `expires_at` ve `provider` alanlari da
eklenebilir. Mevcut form temel aktivasyon sinyali icin yeterlidir.

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
    "ActivationSignalUrl": "https://docs.google.com/forms/d/e/1FAIpQLSdOFrMtIMX3FBXRa0u7eTO00y1w-AYB8EKQ0qMzCQmmcP2oIQ/formResponse",
    "ActivationSignalFields": {
      "license_hash": "entry.1986987783",
      "machine_id": "entry.1100798267",
      "computer_name": "entry.2085233059",
      "activated_at": "entry.471081137",
      "app_version": "entry.1456895206",
      "note": "entry.1183096403"
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

## Lisans iptali

Musterinin lisansini iptal etmek icin key hashini `docs/revoked.txt` dosyasina
tek satir olarak ekle:

```txt
2FFE01326CA563CBD75833DAA2A0F27A49B231258DCC21AFE895A2D31D3D4A88
```

Uygulama acilista, `Dogrula` basildiginda ve yedekleme oncesinde bu dosyayi
kontrol eder. Hash iptal listesindeyse lisans hemen kilitlenir.

Internet yoksa son basarili iptal kontrolu 24 saat boyunca kabul edilir.
24 saat gectikten sonra uygulama iptal listesini tekrar okuyana kadar yedekleme
yaptirmaz.
