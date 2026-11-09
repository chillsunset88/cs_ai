using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CS_AI.Services
{
    /// <summary>
    /// Token sekali pakai untuk link reset password. Token dibuat bot setelah OTP benar dan terikat ke username
    /// yang sudah diverifikasi, sehingga halaman reset tidak perlu (dan tidak boleh) menerima username dari user.
    /// Disimpan di memori: hilang saat app restart, dan itu disengaja (link ikut tidak berlaku).
    /// </summary>
    public class PasswordResetTokenService
    {
        private readonly ConcurrentDictionary<string, Entry> _tokens = new();
        private readonly TimeSpan _lifetime;

        private readonly ILogger<PasswordResetTokenService> _logger;

        public PasswordResetTokenService(IConfiguration configuration, ILogger<PasswordResetTokenService> logger)
        {
            _logger = logger;
            var minutes = configuration.GetValue<int?>("PasswordReset:TokenMinutes") ?? 10;
            _lifetime = TimeSpan.FromMinutes(Math.Max(1, minutes));
        }

        public TimeSpan Lifetime => _lifetime;

        public string Create(string username)
        {
            Prune();

            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            _tokens[token] = new Entry(username, DateTime.UtcNow.Add(_lifetime));

            // TEMP-DIAG: hapus setelah masalah "link langsung expired" selesai.
            _logger.LogWarning("TEMP-DIAG token dibuat: pid={Pid} prefix={Prefix} tokensDiMemori={Count} umur={Lifetime}",
                Environment.ProcessId, token[..6], _tokens.Count, _lifetime);
            return token;
        }

        public bool TryGetUsername(string? token, out string username)
        {
            username = "";
            if (string.IsNullOrWhiteSpace(token) || !_tokens.TryGetValue(token, out var entry))
            {
                // TEMP-DIAG: alasan "tidak ditemukan" (proses berbeda, restart, atau sudah dipakai), bukan "kedaluwarsa".
                _logger.LogWarning("TEMP-DIAG token tidak ditemukan: pid={Pid} prefix={Prefix} tokensDiMemori={Count}",
                    Environment.ProcessId, string.IsNullOrEmpty(token) ? "-" : token[..Math.Min(6, token.Length)], _tokens.Count);
                return false;
            }

            if (entry.ExpiresAt < DateTime.UtcNow)
            {
                _logger.LogWarning("TEMP-DIAG token kedaluwarsa: pid={Pid} prefix={Prefix} lewatDetik={Late}",
                    Environment.ProcessId, token[..6], (DateTime.UtcNow - entry.ExpiresAt).TotalSeconds);
                _tokens.TryRemove(token, out _);
                return false;
            }

            username = entry.Username;
            return true;
        }

        public void Consume(string? token)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                _tokens.TryRemove(token, out _);
            }
        }

        private void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var (key, entry) in _tokens)
            {
                if (entry.ExpiresAt < now)
                {
                    _tokens.TryRemove(key, out _);
                }
            }
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private sealed record Entry(string Username, DateTime ExpiresAt);
    }
}
