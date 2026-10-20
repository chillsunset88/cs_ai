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

        public PasswordResetTokenService(IConfiguration configuration)
        {
            var minutes = configuration.GetValue<int?>("PasswordReset:TokenMinutes") ?? 10;
            _lifetime = TimeSpan.FromMinutes(Math.Max(1, minutes));
        }

        public TimeSpan Lifetime => _lifetime;

        public string Create(string username)
        {
            Prune();

            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            _tokens[token] = new Entry(username, DateTime.UtcNow.Add(_lifetime));
            return token;
        }

        public bool TryGetUsername(string? token, out string username)
        {
            username = "";
            if (string.IsNullOrWhiteSpace(token) || !_tokens.TryGetValue(token, out var entry))
                return false;

            if (entry.ExpiresAt < DateTime.UtcNow)
            {
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
