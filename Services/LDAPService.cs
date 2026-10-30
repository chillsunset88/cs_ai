using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;

namespace CS_AI.Services
{
    /// <summary>Hasil pencarian user di AD.</summary>
    public sealed record LdapUser(string? SamAccountName, string? DisplayName, string DistinguishedName, string? Phone = null);

    /// <summary>Hasil membandingkan nomor telepon yang dibagikan user dengan nomor di AD.</summary>
    public enum PhoneCheck
    {
        Match,
        Mismatch,
        NoPhoneOnRecord,
        UserNotFound
    }

    /// <summary>
    /// Akses Active Directory lewat LDAP/LDAPS (System.DirectoryServices.Protocols), jadi jalan di Windows maupun Linux.
    /// Mengubah password di AD mewajibkan kanal terenkripsi, karena itu LDAPS (port 636) adalah default.
    /// </summary>
    public class LDAPService
    {
        public const string TelegramDummyUsername = "telegram.demo";

        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<LDAPService> _logger;
        private readonly string? _server;
        private readonly string? _username;
        private readonly string? _password;
        private readonly string? _bindDomain;
        private readonly string? _configuredBaseDn;
        private readonly string _phoneAttribute;
        private readonly int _port;
        private readonly bool _useSsl;
        private readonly bool _startTls;

        // defaultNamingContext dibaca sekali dari rootDSE bila Ldap:BaseDn tidak diisi.
        private static string? _discoveredBaseDn;

        public LDAPService(IWebHostEnvironment environment, IConfiguration configuration, ILogger<LDAPService> logger)
        {
            _environment = environment;
            _logger = logger;
            _server = configuration["Ldap:Server"];
            _username = configuration["Ldap:Username"];
            _password = configuration["Ldap:Password"];
            _bindDomain = configuration["Ldap:BindDomain"];
            _configuredBaseDn = configuration["Ldap:BaseDn"];
            _phoneAttribute = string.IsNullOrWhiteSpace(configuration["Ldap:PhoneAttribute"]) ? "mobile" : configuration["Ldap:PhoneAttribute"]!;
            _useSsl = configuration.GetValue<bool?>("Ldap:UseSsl") ?? true;
            _port = configuration.GetValue<int?>("Ldap:Port") ?? (_useSsl ? 636 : 389);
            // StartTLS: koneksi dibuka di port 389 lalu ditingkatkan ke TLS, cukup untuk ganti password tanpa LDAPS.
            _startTls = !_useSsl && (configuration.GetValue<bool?>("Ldap:StartTls") ?? false);
        }

        // Kredensial AD dibaca dari konfigurasi (Ldap:*), bukan hardcoded.
        // Dicek saat dipakai, bukan di constructor, agar mode Development (akun dummy) tidak butuh AD.
        private LdapConnection CreateConnection()
        {
            if (string.IsNullOrWhiteSpace(_server) || string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
            {
                throw new InvalidOperationException("Ldap:Server, Ldap:Username, dan Ldap:Password belum diisi di konfigurasi");
            }

            var connection = new LdapConnection(new LdapDirectoryIdentifier(_server, _port))
            {
                AuthType = AuthType.Basic,
                Credential = new NetworkCredential(BindIdentity(), _password),
                Timeout = TimeSpan.FromSeconds(15)
            };
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.SecureSocketLayer = _useSsl;

            try
            {
                if (_startTls)
                    connection.SessionOptions.StartTransportLayerSecurity(null);
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            try
            {
                // TEMP-DIAG: hapus setelah masalah bind selesai. Password tidak pernah dicatat.
                _logger.LogWarning("TEMP-DIAG sebelum Bind: server={Server} port={Port} ssl={Ssl} starttls={StartTls} authType={AuthType} identity={Identity} passwordLength={PwLen}",
                    _server, _port, _useSsl, _startTls, connection.AuthType, BindIdentity(), _password!.Length);

                connection.Bind();

                _logger.LogWarning("TEMP-DIAG setelah Bind: Bind() selesai tanpa exception");
                try
                {
                    // WhoAmI (RFC 4532): kosong/"anonymous" berarti AD menganggap koneksi ini belum terautentikasi.
                    var who = (ExtendedResponse)connection.SendRequest(new ExtendedRequest("1.3.6.1.4.1.4203.1.11.3"));
                    _logger.LogWarning("TEMP-DIAG WhoAmI: '{Who}'",
                        who.ResponseValue == null ? "" : Encoding.UTF8.GetString(who.ResponseValue));
                }
                catch (Exception whoEx)
                {
                    _logger.LogWarning("TEMP-DIAG WhoAmI gagal: {Type} {Message}", whoEx.GetType().Name, whoEx.Message);
                }
            }
            catch (Exception bindEx)
            {
                _logger.LogWarning("TEMP-DIAG Bind gagal: {Type} {Message}", bindEx.GetType().Name, bindEx.Message);
                connection.Dispose();
                throw;
            }

            return connection;
        }

        // Simple bind AD menerima UPN (user@domain), DN, atau DOMAIN\user. Bila Ldap:Username berupa nama polos
        // dan Ldap:BindDomain diisi, dipakai bentuk DOMAIN\user.
        private string BindIdentity()
        {
            var user = _username!;
            if (!string.IsNullOrWhiteSpace(_bindDomain) && user.IndexOfAny(new[] { '\\', '@', '=' }) < 0)
            {
                return $"{_bindDomain}\\{user}";
            }

            return user;
        }

        private string ResolveBaseDn(LdapConnection connection)
        {
            if (!string.IsNullOrWhiteSpace(_configuredBaseDn))
                return _configuredBaseDn;

            if (_discoveredBaseDn != null)
                return _discoveredBaseDn;

            var request = new SearchRequest(string.Empty, "(objectClass=*)", SearchScope.Base, "defaultNamingContext");
            var response = (SearchResponse)connection.SendRequest(request);
            var value = response.Entries.Count > 0 && response.Entries[0].Attributes.Contains("defaultNamingContext")
                ? response.Entries[0].Attributes["defaultNamingContext"][0] as string
                : null;

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("Base DN tidak ditemukan; isi Ldap:BaseDn di konfigurasi");
            }

            _discoveredBaseDn = value;
            return value;
        }

        // Escape karakter khusus filter LDAP (RFC 4515) agar username dari chat tidak bisa menyisipkan filter sendiri.
        internal static string EscapeFilterValue(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\5c"); break;
                    case '*': sb.Append("\\2a"); break;
                    case '(': sb.Append("\\28"); break;
                    case ')': sb.Append("\\29"); break;
                    case '\0': sb.Append("\\00"); break;
                    default: sb.Append(c); break;
                }
            }

            return sb.ToString();
        }

        private LdapUser? FindUser(LdapConnection connection, string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return null;

            var filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={EscapeFilterValue(username.Trim())}))";
            var request = new SearchRequest(
                ResolveBaseDn(connection),
                filter,
                SearchScope.Subtree,
                "sAMAccountName", "displayName", "distinguishedName", _phoneAttribute);

            var response = (SearchResponse)connection.SendRequest(request);
            if (response.Entries.Count == 0)
                return null;

            var entry = response.Entries[0];
            return new LdapUser(
                ReadAttribute(entry, "sAMAccountName"),
                ReadAttribute(entry, "displayName"),
                entry.DistinguishedName,
                ReadAttribute(entry, _phoneAttribute));
        }

        private static string? ReadAttribute(SearchResultEntry entry, string name) =>
            entry.Attributes.Contains(name) && entry.Attributes[name].Count > 0
                ? entry.Attributes[name][0] as string
                : null;

        public bool TelegramUserExists(string username)
        {
            if (_environment.IsDevelopment())
            {
                return string.Equals(username, TelegramDummyUsername, StringComparison.OrdinalIgnoreCase);
            }

            return GetUser(username) != null;
        }

        // Membandingkan nomor yang dibagikan user (sudah dinormalkan) dengan atribut telepon akun di AD
        // (Ldap:PhoneAttribute, default "mobile"). Di Development hanya akun dummy yang dianggap cocok.
        public PhoneCheck CheckPhone(string username, string normalizedPhone, string defaultCountryCode = "62")
        {
            if (_environment.IsDevelopment())
            {
                return string.Equals(username, TelegramDummyUsername, StringComparison.OrdinalIgnoreCase)
                    ? PhoneCheck.Match
                    : PhoneCheck.UserNotFound;
            }

            var user = GetUser(username);
            if (user == null)
                return PhoneCheck.UserNotFound;

            var adPhone = PhoneNumberHelper.Normalize(user.Phone, defaultCountryCode);
            if (adPhone == null)
                return PhoneCheck.NoPhoneOnRecord;

            return string.Equals(adPhone, normalizedPhone, StringComparison.Ordinal) ? PhoneCheck.Match : PhoneCheck.Mismatch;
        }

        public LdapUser? GetUser(string username)
        {
            using var connection = CreateConnection();
            return FindUser(connection, username);
        }

        public bool ResetTemporaryPassword(string username, string password)
        {
            if (_environment.IsDevelopment())
            {
                return string.Equals(username, TelegramDummyUsername, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(password);
            }

            // pwdLastSet=0 memaksa user mengganti password saat login berikutnya.
            return SetPasswordInternal(username, password, expireNow: true);
        }

        // Dipakai halaman reset yang diakses lewat link bot (token). Di Development hanya akun dummy yang
        // diterima, sama seperti ResetTemporaryPassword, supaya tes lokal tidak menyentuh AD sungguhan.
        public bool SetNewPassword(string username, string password)
        {
            if (_environment.IsDevelopment())
            {
                return string.Equals(username, TelegramDummyUsername, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(password);
            }

            return ResetPassword(username, password);
        }

        public bool ResetPassword(string username, string password) =>
            SetPasswordInternal(username, password, expireNow: false);

        private bool SetPasswordInternal(string username, string password, bool expireNow)
        {
            if (string.IsNullOrEmpty(password))
                return false;

            using var connection = CreateConnection();
            var user = FindUser(connection, username);
            if (user == null)
                return false;

            if (!_useSsl && !_startTls)
            {
                // AD menolak perubahan unicodePwd di kanal tanpa enkripsi; gagal lebih awal dengan pesan jelas.
                throw new InvalidOperationException("Ganti password di AD butuh LDAPS; setel Ldap:UseSsl=true (port 636) atau Ldap:StartTls=true (port 389)");
            }

            // unicodePwd harus berupa string UTF-16LE yang dibungkus tanda kutip ganda.
            var encoded = Encoding.Unicode.GetBytes("\"" + password + "\"");
            var modifications = new List<DirectoryAttributeModification>
            {
                new()
                {
                    Name = "unicodePwd",
                    Operation = DirectoryAttributeOperation.Replace
                }
            };
            modifications[0].Add(encoded);

            if (expireNow)
            {
                var pwdLastSet = new DirectoryAttributeModification
                {
                    Name = "pwdLastSet",
                    Operation = DirectoryAttributeOperation.Replace
                };
                pwdLastSet.Add("0");
                modifications.Add(pwdLastSet);
            }

            try
            {
                connection.SendRequest(new ModifyRequest(user.DistinguishedName, modifications.ToArray()));
                return true;
            }
            catch (DirectoryOperationException ex)
            {
                // Mis. kebijakan password tidak terpenuhi (ConstraintViolation) atau akun layanan tidak punya hak reset.
                _logger.LogWarning("AD menolak perubahan password untuk {Username}: {Result} {Message}",
                    user.SamAccountName, ex.Response?.ResultCode, ex.Message);
                throw;
            }
        }
    }
}
