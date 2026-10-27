# Runbook: menjalankan CS_AI di server produksi Linux (via SSH)

Tujuan: bot Telegram berjalan di server Linux dengan `ASPNETCORE_ENVIRONMENT=Production`, dites dengan username asli.
Menggantikan runbook lama berbasis Windows/RDC (`RUNBOOK-PRODUCTION-RDC.id.md`, masih ada di riwayat git).
Untuk tes tanpa AD (hanya akun dummy `telegram.demo`), setel `ASPNETCORE_ENVIRONMENT=Development`.

> **Peringatan.** Tes dengan username asli mereset password AD sungguhan. Pakai akun uji milik sendiri.
> OTP dikirim ke **WhatsApp** pada nomor yang dibagikan user lewat tombol kontak Telegram (bukan ke chat Telegram).
> Nomor itu hanya membuktikan pemilik akun Telegram tersebut. Agar terikat ke pemilik akun AD, nomor dicocokkan dengan
> atribut telepon akun di AD (`PasswordReset:RequireAdPhoneMatch`, default `true`, lihat bagian 4a).

## 0. Akses Active Directory dari Linux (LDAPS)

`Services/LDAPService.cs` sekarang memakai `System.DirectoryServices.Protocols` (LDAP/LDAPS), sehingga jalan di Linux
maupun Windows. Library lama (`AccountManagement`) hanya untuk Windows dan sudah tidak dipakai oleh bot maupun halaman reset.
Cara kerjanya: bind dengan akun layanan, cari user lewat `sAMAccountName`, lalu ubah atribut `unicodePwd`.

**Syarat agar berhasil:**

1. **LDAPS aktif di domain controller** (port 636) dengan sertifikat yang valid. AD menolak perubahan password di kanal
   tanpa enkripsi, jadi `Ldap:UseSsl` harus `true` (default).
2. **`Ldap:Server` harus berupa nama FQDN** yang cocok dengan sertifikat DC (mis. `dc01.corp.local`), **bukan IP**.
   Sertifikat umumnya tidak memuat IP, sehingga koneksi ke IP gagal validasi.
3. **Sertifikat CA yang menerbitkan sertifikat DC harus dipercaya server Linux** (bagian 2, langkah 4).
4. **Akun layanan** punya hak *Reset Password* pada OU target. Isi `Ldap:Username` dengan bentuk UPN (`svc@corp.local`)
   atau `DOMAIN\user`; nama polos juga bisa jika `Ldap:BindDomain` diisi.
5. `Ldap:BaseDn` boleh dikosongkan (diambil otomatis dari `defaultNamingContext`), atau diisi untuk membatasi pencarian
   ke satu OU, mis. `OU=Karyawan,DC=corp,DC=local`.

> **Status pengujian.** Bagian yang diuji: mode dev (`telegram.demo`), escape filter, pesan error saat konfigurasi kosong,
> dan error koneksi saat server tidak terjangkau, semuanya di Windows. Interaksi dengan AD sungguhan (bind, search,
> `unicodePwd`) dan validasi sertifikat di Linux **belum pernah dijalankan**. Anggap tes dengan akun uji pada runbook ini
> sebagai pengujian pertama.

Endpoint admin lama `UserController` (`/api/User/resetPassword`) masih memakai API Windows dan tidak berfungsi di Linux.
Alur bot Telegram dan halaman `/account/resetpassword` tidak bergantung padanya.

## 1. Keputusan sebelum mulai

| Hal | Rekomendasi |
|---|---|
| **Satu token = satu instance.** Dua proses yang polling dengan token yang sama saling rebut update (Telegram membalas `409 Conflict`). | Matikan app di laptop sebelum menyalakan yang di server, atau buat bot kedua lewat @BotFather khusus tes server. |
| **Siapa yang boleh chat ke bot.** | Pakai bot tes yang username-nya tidak dibagikan. Kode belum punya daftar chat yang diizinkan. |
| **Akun yang dites.** | Satu akun AD uji yang password-nya boleh diubah. |
| **Link reset.** `PasswordReset:BaseUrl` default `http://localhost:5000`, dan Kestrel hanya mendengarkan `localhost:5000`. | Untuk tes, buka link lewat **SSH tunnel** (bagian 6). Agar bisa dibuka dari perangkat user, butuh reverse proxy (nginx/Caddy) dengan TLS dan `PasswordReset__BaseUrl` yang menunjuk ke sana. |

## 2. Prasyarat di server

1. **ASP.NET Core Runtime 8** (bukan hanya SDK). Cek:
   ```bash
   dotnet --list-runtimes
   ```
   Harus ada baris `Microsoft.AspNetCore.App 8.x`. Publish bersifat *framework-dependent*.
2. **ffmpeg** dari paket distro. Aplikasi mencari `Ffmpeg:Path`, lalu `ffmpeg` di PATH:
   ```bash
   sudo apt install -y ffmpeg      # Debian/Ubuntu; RHEL/Rocky: dnf install ffmpeg (butuh repo tambahan)
   ffmpeg -version
   ```
3. **Pustaka native Whisper.** Biasanya cukup bawaan, tetapi pada image minimal sering kurang `libgomp`:
   ```bash
   sudo apt install -y libgomp1
   ```
4. **libldap dan kepercayaan sertifikat AD.** .NET memakai OpenLDAP di Linux:
   ```bash
   sudo apt install -y libldap-2.5-0 ldap-utils ca-certificates    # Ubuntu 24.04: libldap2
   # Pasang sertifikat CA internal (format PEM, berakhiran .crt) ke penyimpanan sistem:
   sudo cp corp-root-ca.crt /usr/local/share/ca-certificates/ && sudo update-ca-certificates
   # Pastikan OpenLDAP memakai penyimpanan sistem:
   grep -q TLS_CACERT /etc/ldap/ldap.conf || echo 'TLS_CACERT /etc/ssl/certs/ca-certificates.crt' | sudo tee -a /etc/ldap/ldap.conf
   ```
   Uji dengan akun layanan sebelum menjalankan app (harus mengembalikan satu `dn:`):
   ```bash
   ldapsearch -H ldaps://dc01.corp.local -D 'svc@corp.local' -W -b 'DC=corp,DC=local' '(sAMAccountName=akun.uji)' dn
   ```
   Jika gagal dengan error sertifikat, perbaiki rantai CA dulu; app akan gagal dengan sebab yang sama.
5. **Koneksi keluar** (jalankan dari server):
   ```bash
   for h in api.telegram.org console-aibridge.visionet.co.id generativelanguage.googleapis.com api.kirimi.id; do
     printf '%s ' "$h"; curl -sS -o /dev/null -m 10 -w '%{http_code}\n' "https://$h" || echo GAGAL
   done
   nc -zv -w 5 dc01.corp.local 636    # AD via LDAPS (ganti dengan FQDN DC Anda)
   ```
   Kode HTTP apa pun (termasuk 401/404) berarti koneksi tembus; `GAGAL` berarti diblokir. Kalau hanya AD yang gagal,
   reset password tidak akan jalan.
6. **Ruang disk** sekitar 1 GB (publish ±500 MB, hampir semuanya model Whisper `ggml-small.bin`).

## 3. Build dan kirim ke server

Di mesin dev:

```bash
dotnet publish CS_AI.csproj -c Release -r linux-x64 --self-contained false -o ./out/CS_AI
```

Sebelum dikirim:

- Kosongkan `out/CS_AI/wwwroot/tts` dan `out/CS_AI/wwwroot/voices` (audio uji lama) serta `wwwroot/OTP.txt`.
- Pastikan `wwwroot/database.txt` berisi data billing yang dimaksud.
- Hapus folder `out/CS_AI/publish` kalau ada (sisa hasil publish lama yang ikut tersalin dari folder project).
- `appsettings.json` di dalam hasil publish memuat nilai dari mesin dev. Sebaiknya ganti dengan
  `appsettings.example.json` yang dikosongkan, lalu isi rahasia lewat environment file (bagian 4).

Kirim ke server:

```bash
ssh user@server 'sudo mkdir -p /opt/cs-ai && sudo chown $USER /opt/cs-ai'
rsync -az --delete ./out/CS_AI/ user@server:/opt/cs-ai/
```

Rilis berikutnya: tambahkan `--exclude wwwroot/` supaya model 466 MB dan data tidak ikut dikirim ulang.

Buat user layanan dan beri hak tulis pada folder yang ditulis aplikasi (`wwwroot/tts`, `wwwroot/voices`, `wwwroot/OTP.txt`):

```bash
sudo useradd --system --home /opt/cs-ai --shell /usr/sbin/nologin csai
sudo chown -R csai:csai /opt/cs-ai
```

## 4. Konfigurasi di server

Simpan rahasia di file environment, **bukan** di `appsettings.json`. Tanda `:` ditulis `__`.

```bash
sudo mkdir -p /etc/cs-ai
sudo tee /etc/cs-ai/cs-ai.env >/dev/null <<'EOF'
ASPNETCORE_ENVIRONMENT=Production
Telegram__BotToken=<token bot yang dipakai di server>
Ffmpeg__Path=/usr/bin/ffmpeg
Ldap__Server=<FQDN domain controller, mis. dc01.corp.local>
Ldap__Username=<akun layanan, mis. svc@corp.local>
Ldap__Password=<password akun layanan>
# Opsional: Ldap__Port=636  Ldap__UseSsl=true  Ldap__BindDomain=CORP  Ldap__BaseDn=OU=Karyawan,DC=corp,DC=local
# Pengiriman OTP lewat WhatsApp (kirimi.id) -- WAJIB agar OTP terkirim:
WhatsApp__UserCode=<user code kirimi>
WhatsApp__DeviceId=<device id kirimi>
WhatsApp__Secret=<secret kirimi>
LLM__ApiKey=<...>
Gemini__ApiKey=<...>
# PasswordReset__BaseUrl=http://localhost:5000
EOF
sudo chown root:csai /etc/cs-ai/cs-ai.env && sudo chmod 640 /etc/cs-ai/cs-ai.env
```

Untuk opsi C, ubah baris pertama menjadi `ASPNETCORE_ENVIRONMENT=Development`.
Kunci lain (WhatsApp, `Telegram__MaxConcurrentUpdates`, dst.) mengikuti `appsettings.example.json`.

### 4a. OTP lewat WhatsApp dan pencocokan nomor di AD

Alur: user mengirim username → bot menampilkan tombol **Bagikan nomor saya** → user menekannya → bot mengirim OTP ke
WhatsApp pada nomor itu → user membalas OTP di Telegram → bot mengirim link reset.

- Nomor diterima hanya jika kontak itu milik akun Telegram yang sedang chat (`Contact.UserId == From.Id`).
- Nomor dinormalkan ke format `628xxxxxxxxxx` (`0812…`, `+62 812…`, `6281…` dianggap sama). Kode negara bawaan `62`
  (`PasswordReset__DefaultCountryCode`).
- **`PasswordReset__RequireAdPhoneMatch=true` (default):** nomor harus sama dengan atribut telepon akun di AD
  (`Ldap__PhoneAttribute`, default `mobile`). Tanpa ini, siapa pun yang tahu sebuah username bisa memakai nomornya
  sendiri untuk mereset akun itu. Jika atribut di AD kosong atau berbeda, reset ditolak dengan pesan umum; alasan
  sebenarnya hanya ada di log (`Verifikasi nomor gagal ... NoPhoneOnRecord/Mismatch`).
- Untuk tes dengan akun yang atribut teleponnya belum diisi, sementara setel `PasswordReset__RequireAdPhoneMatch=false`.
  Itu melepas pengikatan ke AD, jadi jangan dibiarkan untuk pemakaian sebenarnya.
- Di mode `Development`, hanya akun dummy `telegram.demo` yang dianggap cocok; OTP tetap dikirim ke nomor yang dibagikan.
- Format penerima WhatsApp yang dipakai adalah nomor dengan kode negara (`628…`). Konfigurasi lama `WhatsApp__Receiver`
  (ID `@lid`) hanya dipakai untuk notifikasi admin, bukan OTP. Belum diuji terhadap gateway kirimi.id sungguhan.

## 5. Jalankan

**Uji di foreground dulu** (hentikan dengan Ctrl+C):

```bash
cd /opt/cs-ai
sudo -u csai bash -c 'set -a; . /etc/cs-ai/cs-ai.env; set +a; exec dotnet CS_AI.dll'
```

Tanda sehat di log: `Bot Started : <username_bot>`, tanpa `Telegram bot cannot reach Telegram API` berulang.

**Lalu pasang sebagai service systemd** (`/etc/systemd/system/cs-ai.service`):

```ini
[Unit]
Description=CS_AI Telegram bot
After=network-online.target
Wants=network-online.target

[Service]
User=csai
# WAJIB: kode membaca wwwroot/ lewat folder kerja (database.txt, tts, voices).
WorkingDirectory=/opt/cs-ai
ExecStart=/usr/bin/dotnet /opt/cs-ai/CS_AI.dll
EnvironmentFile=/etc/cs-ai/cs-ai.env
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=cs-ai

[Install]
WantedBy=multi-user.target
```

Cek lokasi `dotnet` dengan `which dotnet` dan sesuaikan `ExecStart` bila bukan `/usr/bin/dotnet`.

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now cs-ai
systemctl status cs-ai --no-pager
journalctl -u cs-ai -f          # log langsung
```

## 6. Checklist tes

Dengan akun uji (opsi C: `telegram.demo`):

1. `/start` dan `/time` dijawab bot.
2. Kirim teks "ganti password saya" → bot meminta username.
3. Kirim username → bot menampilkan tombol **Bagikan nomor saya**.
4. Tekan tombol itu → OTP 6 digit datang di **WhatsApp** pada nomor tersebut (bukan di Telegram).
5. Balas OTP di Telegram → bot mengirim link `.../account/resetpassword?token=...`.
6. Buka link lewat **SSH tunnel** dari laptop:
   ```bash
   ssh -L 5000:localhost:5000 user@server
   ```
   lalu buka link tadi di browser laptop (host `localhost:5000` sudah cocok).
7. Isi password baru yang memenuhi kebijakan AD, kirim, dan pastikan halaman sukses muncul. Login dengan password baru.
8. Buka link yang sama lagi → harus muncul "tidak valid atau kedaluwarsa".
9. Ulangi dengan **voice note** ("tolong ganti password saya"). Tombol kontak tetap muncul sebagai teks, OTP datang di WhatsApp, dan link tetap dikirim sebagai teks.
10. Uji negatif:
    - username yang tidak ada → bot minta kirim ulang;
    - membagikan kontak **orang lain** (teruskan kontak) → ditolak, tidak ada WhatsApp terkirim;
    - nomor yang berbeda dari nomor di AD (saat `RequireAdPhoneMatch=true`) → "tidak dapat diverifikasi", tidak ada WhatsApp;
    - mengetik teks saat tombol kontak diminta → bot mengingatkan, tidak dilempar ke LLM;
    - OTP salah 3 kali → OTP hangus.
11. Billing (nomor yang ada di `database.txt`) dan laporan jalan rusak masih jalan.

## 7. Selesai tes

```bash
sudo systemctl disable --now cs-ai
sudo shred -u /etc/cs-ai/cs-ai.env      # bila tidak dipakai lagi
```

Ganti password akun uji, putar (rotate) token dan kunci yang sempat dipakai, dan nyalakan app di laptop lagi hanya
setelah service di server berhenti.

## 8. Troubleshooting

| Gejala | Kemungkinan penyebab |
|---|---|
| Bot membalas "terjadi kendala" untuk username asli; log `LdapException` "The LDAP server is unavailable" | DC tidak terjangkau di port 636, `Ldap__Server` salah, atau LDAPS belum aktif di DC. |
| `LdapException` soal sertifikat / "Can't contact LDAP server" padahal port terbuka | Sertifikat CA belum dipercaya server (bagian 2, langkah 4), atau `Ldap__Server` berupa IP, bukan FQDN. |
| `LdapException` "Invalid Credentials" | `Ldap__Username`/`Password` salah, atau bentuk username tidak diterima; coba UPN atau isi `Ldap__BindDomain`. |
| `DirectoryOperationException` `InsufficientAccessRights` | Akun layanan belum punya hak *Reset Password* pada OU user. |
| `DirectoryOperationException` `ConstraintViolation` | Password baru tidak memenuhi kebijakan AD (panjang, kompleksitas, riwayat). |
| `DllNotFoundException` untuk `libldap` | `sudo apt install libldap-2.5-0` (atau `libldap2`). |
| Username yang benar dijawab "tidak ditemukan" | `Ldap__BaseDn` terlalu sempit, atau user berada di luar OU itu. |
| Bot tidak menjawab / log `409 Conflict` | Ada instance lain dengan token yang sama (laptop, server lain). |
| Voice note gagal; log `FFmpeg not found` atau `Exec format error` / `Permission denied` | ffmpeg belum terpasang, atau `Ffmpeg__Path` menunjuk ke `ffmpeg.exe` Windows. Isi `Ffmpeg__Path=/usr/bin/ffmpeg`. |
| Voice note gagal; log `libgomp.so.1: cannot open shared object file` | `sudo apt install libgomp1`. |
| Error `FileNotFoundException` untuk `wwwroot/database.txt` atau `Access to the path ... tts/voices denied` | `WorkingDirectory` di unit systemd salah, atau `/opt/cs-ai` tidak dimiliki user `csai`. |
| `Address already in use` pada port 5000 | `ss -ltnp \| grep 5000`; ada proses lain atau instance ganda. |
| Variabel di env file tidak terbaca | Nama kunci harus memakai `__`, tanpa spasi di sekitar `=`, dan tanpa tanda kutip yang tidak perlu. |
| Link dibuka dari HP tidak jalan | `localhost` menunjuk ke HP itu sendiri; pakai SSH tunnel atau reverse proxy dengan `PasswordReset__BaseUrl`. |
| Balasan voice berupa teks | TTS Gemini gagal (kuota/jaringan); bot sengaja jatuh ke teks. Lihat log `TTS failed`. |

## 9. Catatan keamanan

- `appsettings.json` di mesin dev berisi kredensial asli. Jangan ikut dikirim ke server; putar kredensial setelah tes.
- Swagger aktif juga di Production (`Program.cs`). Selama Kestrel hanya `localhost`, ia hanya terbuka dari dalam server;
  jangan buka port 5000 ke jaringan tanpa mematikannya.
- Endpoint `/api/WebHook/Receive` tidak punya autentikasi, dan halaman `/account/resetpassword` **tanpa token** masih
  bisa mereset akun mana pun.
