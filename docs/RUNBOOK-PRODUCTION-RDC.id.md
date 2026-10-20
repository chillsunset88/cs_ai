# Runbook: menjalankan CS_AI di server produksi (via RDC) untuk tes dengan username asli

Tujuan: bot Telegram berjalan di server yang bisa menjangkau Active Directory, dengan `ASPNETCORE_ENVIRONMENT=Production`,
sehingga alur ganti password benar-benar memakai akun AD (bukan akun dummy `telegram.demo`).

> **Peringatan.** Tes ini mereset password AD sungguhan. Pakai akun uji milik sendiri, bukan akun orang lain.
> OTP dikirim ke chat yang sama dengan yang meminta reset, jadi siapa pun yang bisa chat ke bot dan tahu sebuah username
> bisa mereset akun itu. Batasi akses ke bot selama tes (lihat bagian 0).

## 0. Keputusan sebelum mulai

| Hal | Rekomendasi |
|---|---|
| **Satu token = satu instance.** Dua proses yang polling dengan token yang sama saling rebut update (Telegram membalas `409 Conflict`). | Matikan app di laptop sebelum menyalakan yang di server. Atau buat bot kedua lewat @BotFather khusus tes server. |
| **Siapa yang boleh chat ke bot.** | Pakai bot tes yang username-nya tidak dibagikan. Kode saat ini belum punya daftar chat yang diizinkan. |
| **Akun yang dites.** | Satu akun AD uji yang password-nya boleh diubah. |
| **Link reset.** `PasswordReset:BaseUrl` default `http://localhost:5000`. | Untuk tes pertama, biarkan `localhost` dan buka link di browser **di dalam sesi RDC**. Agar bisa dibuka dari HP/PC user, butuh reverse proxy atau binding lain (Kestrel saat ini hanya `localhost:5000`). |

## 1. Prasyarat di server

1. **.NET 8 ASP.NET Core Runtime** (bukan hanya SDK). Cek:
   ```powershell
   dotnet --list-runtimes
   ```
   Harus ada baris `Microsoft.AspNetCore.App 8.x`. Hasil publish bersifat *framework-dependent*, jadi runtime ini wajib ada.
2. **Koneksi keluar dari server** (jalankan dari PowerShell di server):
   ```powershell
   Test-NetConnection api.telegram.org -Port 443
   Test-NetConnection console-aibridge.visionet.co.id -Port 443
   Test-NetConnection generativelanguage.googleapis.com -Port 443
   Test-NetConnection api.kirimi.id -Port 443
   Test-NetConnection 10.254.210.12 -Port 389
   ```
   Semua harus `TcpTestSucceeded : True`. Kalau yang gagal hanya AD, reset password tidak akan jalan.
3. **Ruang disk** sekitar 1,5 GB (publish 1,1 GB karena model Whisper 466 MB dan dua salinan ffmpeg).
4. **Hak akun AD layanan.** Akun yang dipakai di `LDAPService.cs` (`admin.aiops`) harus punya hak *Reset Password* pada OU target.

## 2. Build dan kirim ke server

Di mesin dev:

```powershell
dotnet publish C:\Users\Geoffrey\CS_AI\CS_AI.csproj -c Release -o D:\publish\CS_AI
```

Lalu, sebelum disalin:

- Kosongkan isi `D:\publish\CS_AI\wwwroot\tts` dan `...\wwwroot\voices` (file audio uji lama).
- Kosongkan `wwwroot\OTP.txt`.
- Pastikan `wwwroot\database.txt` berisi data billing yang dimaksud.
- Jangan hapus salah satu `ffmpeg.exe`: STT memakai `wwwroot\ffmpeg\ffmpeg.exe`, TTS memakai `wwwroot\ffmpeg\bin\ffmpeg.exe`.

Salin ke server lewat RDC (Local Resources → Drives, atau copy-paste folder), mis. ke `D:\Apps\CS_AI`.
Rilis berikutnya cukup menimpa `CS_AI.dll` (dan file `.dll` lain yang berubah); folder `wwwroot` tidak perlu disalin ulang.

## 3. Konfigurasi di server

Yang **wajib**: `ASPNETCORE_ENVIRONMENT=Production`. Jika tidak, `LDAPService` menganggap ini Development dan hanya menerima username `telegram.demo`.

Setel lewat environment variable (menimpa nilai di `appsettings.json`; tanda `:` ditulis `__`):

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:Telegram__BotToken     = "<token bot yang dipakai di server>"
# Opsional, kalau berbeda dari appsettings.json:
# $env:PasswordReset__BaseUrl = "http://localhost:5000"
# $env:LLM__ApiKey            = "<...>"
# $env:Gemini__ApiKey         = "<...>"
```

Variabel di atas hanya berlaku untuk jendela PowerShell itu. Untuk permanen, pakai `[Environment]::SetEnvironmentVariable("NAMA","nilai","Machine")`.

## 4. Jalankan

```powershell
cd D:\Apps\CS_AI
dotnet .\CS_AI.dll
```

Tanda sehat di log:

- `Bot Started : <username_bot>`
- tidak ada `Telegram bot cannot reach Telegram API` berulang

Saat menutup RDC, pilih **disconnect** (tutup jendela RDC), bukan *sign out*, supaya proses tetap hidup. Kebijakan server bisa tetap mengakhiri sesi yang terputus setelah beberapa waktu.
Untuk tes yang lebih lama, jalankan lewat Task Scheduler (trigger *At startup*, jalankan `dotnet D:\Apps\CS_AI\CS_AI.dll`) atau NSSM.

## 5. Checklist tes

Lakukan dengan akun AD uji:

1. `/start` dan `/time` dijawab bot.
2. Kirim teks "ganti password saya" → bot meminta username.
3. Kirim username asli → bot mengirim OTP 6 digit sebagai teks.
4. Balas OTP → bot mengirim link `.../account/resetpassword?token=...`.
5. Buka link di browser **server**, isi password baru yang memenuhi kebijakan AD, lalu kirim.
6. Halaman sukses muncul. Login dengan password baru.
7. Link yang sama dibuka lagi → harus muncul "tidak valid atau kedaluwarsa".
8. Ulangi dengan **voice note** ("tolong ganti password saya"). OTP dan link tetap datang sebagai teks.
9. Uji negatif: username yang tidak ada → bot minta kirim ulang; OTP salah 3 kali → OTP hangus.
10. Billing (nomor yang ada di `database.txt`) dan laporan jalan rusak masih jalan.

Verifikasi di AD (jika modul RSAT ada):

```powershell
Get-ADUser <username> -Properties PasswordLastSet | Select-Object SamAccountName, PasswordLastSet
```

## 6. Selesai tes

1. Hentikan proses (Ctrl+C di jendela PowerShell, atau stop Scheduled Task).
2. Hapus environment variable yang berisi token jika tidak dipakai lagi.
3. Ganti password akun uji.
4. Jika memakai bot yang sama dengan laptop, nyalakan lagi app di laptop hanya setelah server berhenti.

## 7. Troubleshooting

| Gejala | Kemungkinan penyebab |
|---|---|
| Bot tidak menjawab / log `409 Conflict` | Ada instance lain dengan token yang sama (laptop, server lain). Matikan salah satunya. |
| Bot menjawab dengan perilaku lama | Instance lama masih hidup, atau DLL lama yang ter-copy. |
| "User tidak ditemukan" untuk username yang benar | Environment masih `Development` (hanya `telegram.demo` yang dikenali), atau server tidak bisa menjangkau `10.254.210.12`. |
| Halaman reset: "Failed to reset password" / error | Akun layanan tidak punya hak reset pada OU itu, password baru tidak memenuhi kebijakan, atau koneksi ke AD putus. |
| Link dibuka dari HP tidak jalan | `localhost` menunjuk ke HP itu sendiri; lihat bagian 0 tentang `BaseUrl`. |
| Voice note gagal | `ggml-small.bin` atau `ffmpeg.exe` tidak ikut tersalin; cek kedua path ffmpeg di bagian 2. |
| Balasan voice berupa teks | TTS Gemini gagal (kuota/jaringan); bot sengaja jatuh ke teks. Lihat log `TTS failed`. |
| `dotnet` tidak dikenali / app tidak mau start | ASP.NET Core Runtime 8 belum terpasang. |

## 8. Catatan keamanan untuk lingkungan produksi

- `appsettings.json` dan beberapa file kode berisi kredensial asli (token bot, kunci LLM/Gemini, password AD). Setelah tes, putar (rotate) kredensial itu dan pindahkan ke environment variable atau secret store.
- Swagger aktif juga di Production (`Program.cs`). Selama Kestrel hanya `localhost`, ia hanya terbuka dari dalam server; jangan buka port 5000 ke jaringan tanpa mematikannya.
- Endpoint `/api/WebHook/Receive` tidak punya autentikasi, dan halaman `/account/resetpassword` **tanpa token** masih bisa mereset akun mana pun.
