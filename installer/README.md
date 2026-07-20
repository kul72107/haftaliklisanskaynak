# MYedek Setup

Bu klasor bootstrap installer kaynaklarini tutar.

`MYedekSetup.exe` uygulamayi icine gommez. Calistiginda:

1. `https://raw.githubusercontent.com/kul72107/Yedek-app/main/latest.json` dosyasini indirir.
2. Manifestteki en guncel release ZIP dosyasini indirir.
3. SHA256 dogrulamasi yapar.
4. Varsayilan olarak `%LOCALAPPDATA%\ModernYedek` klasorune kurar.
5. Secimler kaldirilmadiysa masaustu ve Baslat Menusu kisayollarini olusturur.

Installer build:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

Varsayilan cikti:

```txt
D:\BITCH\Yedek-app\MYedekSetup.exe
```
