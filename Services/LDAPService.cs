using System.DirectoryServices.AccountManagement;
using Microsoft.AspNetCore.Hosting;

namespace CS_AI.Services
{
    public class LDAPService
    {
        public const string TelegramDummyUsername = "telegram.demo";

        private readonly IWebHostEnvironment _environment;
        private readonly string? _server;
        private readonly string? _username;
        private readonly string? _password;

        public LDAPService(IWebHostEnvironment environment, IConfiguration configuration)
        {
            _environment = environment;
            _server = configuration["Ldap:Server"];
            _username = configuration["Ldap:Username"];
            _password = configuration["Ldap:Password"];
        }

        // Kredensial AD dibaca dari konfigurasi (Ldap:Server/Username/Password), bukan hardcoded.
        // Dicek saat dipakai, bukan di constructor, agar mode Development (akun dummy) tidak butuh AD.
        private PrincipalContext CreateContext()
        {
            if (string.IsNullOrWhiteSpace(_server) || string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
            {
                throw new InvalidOperationException("Ldap:Server, Ldap:Username, dan Ldap:Password belum diisi di konfigurasi");
            }

            return new PrincipalContext(ContextType.Domain, _server, _username, _password);
        }

        public bool TelegramUserExists(string username)
        {
            if (_environment.IsDevelopment())
            {
                return string.Equals(username, TelegramDummyUsername, StringComparison.OrdinalIgnoreCase);
            }

            using var user = GetUser(username);
            return user != null;
        }

        public UserPrincipal? GetUser(string username)
        {
            using var context = CreateContext();

            return UserPrincipal.FindByIdentity(
                context,
                IdentityType.SamAccountName,
                username
            );
        }

        public bool ResetTemporaryPassword(string username, string password)
        {
            if (_environment.IsDevelopment())
            {
                return string.Equals(username, TelegramDummyUsername, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(password);
            }

            using var context = CreateContext();
            using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username);
            if (user == null)
            {
                return false;
            }

            user.SetPassword(password);
            user.ExpirePasswordNow();
            user.Save();
            return true;
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

        public bool ResetPassword(string username, string password)
        {
            using (var context = CreateContext())
            {
                using (var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username))
                {
                    if (user != null)
                    {
                        user.SetPassword(password);
                        user.Save();

                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
        }
    }
}
