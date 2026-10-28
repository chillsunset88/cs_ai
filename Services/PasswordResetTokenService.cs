using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CS_AI.Services
{
    public enum OtpResult { Ok, Invalid, Locked, Expired }

    /// <summary>
    /// Sesi reset password. Bot membuat sesi (token + OTP) untuk username yang sudah ada; token dikirim lewat link,
    /// OTP dikirim lewat WhatsApp. Di halaman web user memasukkan OTP, dan hanya sesi yang OTP-nya sudah benar yang
    /// boleh mengganti password. Username selalu berasal dari sesi, bukan dari form.
    /// Disimpan di memori: hilang saat app restart, dan itu disengaja (link ikut tidak berlaku).
    /// </summary>
    public class PasswordResetTokenService
    {
        private const int MaxOtpAttempts = 3;

        private readonly ConcurrentDictionary<string, Entry> _tokens = new();
        private readonly TimeSpan _lifetime;
        private readonly TimeSpan _otpLifetime;

        public PasswordResetTokenService(IConfiguration configuration)
        {
            var minutes = configuration.GetValue<int?>("PasswordReset:TokenMinutes") ?? 10;
            _lifetime = TimeSpan.FromMinutes(Math.Max(1, minutes));
            var otpMinutes = configuration.GetValue<int?>("PasswordReset:OtpMinutes") ?? 5;
            _otpLifetime = TimeSpan.FromMinutes(Math.Max(1, otpMinutes));
        }

        public TimeSpan Lifetime => _lifetime;

        public string Create(string username, string otp)
        {
            Prune();

            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            var now = DateTime.UtcNow;
            _tokens[token] = new Entry(username, otp, now.Add(_lifetime), now.Add(_otpLifetime));
            return token;
        }

        public bool TryGetUsername(string? token, out string username)
        {
            username = "";
            if (!TryGetEntry(token, out var entry))
                return false;

            username = entry.Username;
            return true;
        }

        public bool IsVerified(string? token) => TryGetEntry(token, out var entry) && entry.Verified;

        public OtpResult VerifyOtp(string? token, string? otp)
        {
            if (!TryGetEntry(token, out var entry))
                return OtpResult.Expired;

            lock (entry)
            {
                if (entry.Verified)
                    return OtpResult.Ok;

                if (entry.OtpExpiresAt < DateTime.UtcNow)
                    return OtpResult.Expired;

                if (entry.Attempts >= MaxOtpAttempts)
                    return OtpResult.Locked;

                var given = Encoding.UTF8.GetBytes((otp ?? "").Trim());
                var expected = Encoding.UTF8.GetBytes(entry.Otp);
                if (!CryptographicOperations.FixedTimeEquals(given, expected))
                {
                    entry.Attempts++;
                    return entry.Attempts >= MaxOtpAttempts ? OtpResult.Locked : OtpResult.Invalid;
                }

                // Beri waktu penuh untuk menulis password baru setelah OTP benar.
                entry.Verified = true;
                entry.ExpiresAt = DateTime.UtcNow.Add(_lifetime);
                return OtpResult.Ok;
            }
        }

        public void Consume(string? token)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                _tokens.TryRemove(token, out _);
            }
        }

        private bool TryGetEntry(string? token, out Entry entry)
        {
            entry = null!;
            if (string.IsNullOrWhiteSpace(token) || !_tokens.TryGetValue(token, out var found))
                return false;

            if (found.ExpiresAt < DateTime.UtcNow)
            {
                _tokens.TryRemove(token, out _);
                return false;
            }

            entry = found;
            return true;
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

        private sealed class Entry
        {
            public Entry(string username, string otp, DateTime expiresAt, DateTime otpExpiresAt)
            {
                Username = username;
                Otp = otp;
                ExpiresAt = expiresAt;
                OtpExpiresAt = otpExpiresAt;
            }

            public string Username { get; }
            public string Otp { get; }
            public DateTime ExpiresAt { get; set; }
            public DateTime OtpExpiresAt { get; }
            public int Attempts { get; set; }
            public bool Verified { get; set; }
        }
    }
}
