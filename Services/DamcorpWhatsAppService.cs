using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CS_AI.Services
{
    /// <summary>
    /// Klien WhatsApp Business API Damcorp (dokumentasi v1.6): login Basic Auth ke /v2/users/login untuk mendapat
    /// token, lalu POST /v2/messages dengan Bearer token. Kredensial hanya dibaca dari konfigurasi (env var
    /// Damcorp__Username / Damcorp__Password), tidak pernah ditulis di source code maupun log.
    /// </summary>
    public class DamcorpWhatsAppService
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
        // Tanpa ini "+62..." diserialisasi menjadi "+62..."; valid, tapi lebih aman dikirim apa adanya.
        private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DamcorpWhatsAppService> _logger;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);

        private string? _token;
        private DateTime _tokenExpiresAt = DateTime.MinValue;

        public DamcorpWhatsAppService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<DamcorpWhatsAppService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Kirim OTP ke nomor WhatsApp (format 628xxxxxxxxxx). Bila Damcorp:OtpTemplateName diisi, dipakai template
        /// autentikasi (bisa menjangkau nomor yang belum pernah chat dengan kita); jika tidak, pesan teks biasa
        /// (hanya terkirim kalau nomor tersebut sedang dalam jendela 24 jam percakapan).
        /// </summary>
        public async Task<bool> SendOtpAsync(string phone, string otp, CancellationToken cancellationToken = default)
        {
            var to = "+" + phone.TrimStart('+');
            var templateName = _configuration["Damcorp:OtpTemplateName"];

            object payload = string.IsNullOrWhiteSpace(templateName)
                ? new
                {
                    messaging_product = "whatsapp",
                    recipient_type = "individual",
                    to,
                    type = "text",
                    text = new
                    {
                        preview_url = false,
                        body = $"Kode OTP reset password Anda: {otp}\nBerlaku {OtpMinutes} menit. Jangan bagikan kode ini kepada siapa pun."
                    }
                }
                : new
                {
                    messaging_product = "whatsapp",
                    recipient_type = "individual",
                    to,
                    type = "template",
                    template = new
                    {
                        name = templateName,
                        language = new { code = _configuration["Damcorp:TemplateLanguage"] ?? "id" },
                        components = new object[]
                        {
                            new { type = "body", parameters = new object[] { new { type = "text", text = otp } } },
                            new { type = "button", sub_type = "url", index = "0", parameters = new object[] { new { type = "text", text = otp } } }
                        }
                    }
                };

            try
            {
                var (status, body) = await PostAsync("/v2/messages", payload, cancellationToken);

                // Token bisa dicabut sebelum expires_after; login ulang sekali lalu coba lagi.
                if (status == System.Net.HttpStatusCode.Unauthorized)
                {
                    InvalidateToken();
                    (status, body) = await PostAsync("/v2/messages", payload, cancellationToken);
                }

                if ((int)status is >= 200 and < 300 && HasMessageId(body))
                    return true;

                // Isi respons error dari Damcorp berisi alasan penolakan (mis. template belum disetujui); OTP tidak ikut.
                _logger.LogWarning("Damcorp menolak pengiriman OTP ke {Phone}: HTTP {Status} {Body}",
                    PhoneNumberHelper.Mask(phone), (int)status, Truncate(body));
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                _logger.LogError(ex, "Pengiriman OTP via Damcorp gagal untuk {Phone}", PhoneNumberHelper.Mask(phone));
                return false;
            }
        }

        private int OtpMinutes => _configuration.GetValue<int?>("PasswordReset:OtpMinutes") ?? 5;

        private async Task<(System.Net.HttpStatusCode Status, string Body)> PostAsync(string path, object payload, CancellationToken cancellationToken)
        {
            var token = await GetTokenAsync(cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RequestTimeout);

            using var response = await _httpClientFactory.CreateClient().SendAsync(request, cts.Token);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cts.Token));
        }

        private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            if (_token != null && DateTime.UtcNow < _tokenExpiresAt)
                return _token;

            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                if (_token != null && DateTime.UtcNow < _tokenExpiresAt)
                    return _token;

                var username = _configuration["Damcorp:Username"];
                var password = _configuration["Damcorp:Password"];
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                    throw new InvalidOperationException("Damcorp:Username dan Damcorp:Password belum diisi.");

                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/v2/users/login");
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(RequestTimeout);

                using var response = await _httpClientFactory.CreateClient().SendAsync(request, cts.Token);
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Login Damcorp gagal: HTTP {(int)response.StatusCode}.");

                using var doc = JsonDocument.Parse(body);
                var user = doc.RootElement.GetProperty("users")[0];
                _token = user.GetProperty("token").GetString()
                    ?? throw new InvalidOperationException("Respons login Damcorp tidak berisi token.");

                // Pakai expires_after bila ada, dikurangi 1 menit agar tidak memakai token yang hampir habis.
                _tokenExpiresAt = user.TryGetProperty("expires_after", out var exp)
                    && DateTime.TryParse(exp.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed.AddMinutes(-1)
                    : DateTime.UtcNow.AddMinutes(30);

                return _token;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private void InvalidateToken()
        {
            _token = null;
            _tokenExpiresAt = DateTime.MinValue;
        }

        private string BaseUrl => (_configuration["Damcorp:BaseUrl"] ?? "https://waba.damcorp.id").TrimEnd('/');

        private static bool HasMessageId(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("messages", out var messages)
                    && messages.ValueKind == JsonValueKind.Array
                    && messages.GetArrayLength() > 0;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
    }
}
