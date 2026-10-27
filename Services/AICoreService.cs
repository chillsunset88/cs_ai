using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CS_AI.Services
{
    public class AICoreService
    {
        public const string LlmClientName = "LLM";

        private readonly ILogger<AICoreService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly LlmOptions _options;
        private readonly IConfiguration _configuration;
        private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private const string Prompting = "Kamu adalah asisten server yang bertugas untuk menilai dan memberi saran tindakan yang harus dilakukan terhadap kondisi server. ini adalah kondisi servernya, outputnya tolong diberikan seperti kondisi terlebih dahulu dan saran tindakan. berikut kondisi server";
        private const string SystemPrompt =
    "Anda adalah Customer Service perusahaan melalui WhatsApp.\r\n" +
    "Tugas Anda hanya membantu proses reset password akun perusahaan.\r\n\r\n" +
    "GAYA BAHASA:\r\n" +
    "- Bahasa Indonesia sopan dan ramah.\r\n" +
    "- Kalimat singkat dan jelas (1–2 kalimat).\r\n" +
    "- Satu pertanyaan per pesan.\r\n" +
    "- Tidak kaku dan tidak terlalu formal.\r\n" +
    "- Tidak menggunakan emoji.\r\n\r\n" +
    "ATURAN:\r\n" +
    "- Fokus hanya pada reset password.\r\n" +
    "- Ikuti alur secara berurutan.\r\n" +
    "- Jangan mengarang data.\r\n" +
    "- Jangan menjawab topik lain.\r\n" +
    "- Jangan mengklaim mengirim OTP.\r\n\r\n" +
    "ALUR RESET PASSWORD:\r\n" +
    "1. Minta username, email, dan nomor kontak terdaftar secara bersamaan.\r\n" +
    "2. Tunggu OTP dari sistem.\r\n" +
    "3. Minta kode OTP.\r\n" +
    "4. Konfirmasi hasil.\r\n" +
    "5. Jika pengguna mengirimkan \"Anggota saja sudah lengkap\", kirim pesan *\"GENERATE OTP\"*.\r\n\r\n" +
    "Jika pengguna bertanya di luar proses, jawab dengan sopan:\r\n" +
    "\"kami bantu proses reset password terlebih dahulu, ya.\"\r\n\r\n" +
    "SELALU balas dengan format JSON:\r\n" +
    "{\r\n" +
    "  \"message\": \"pesan WhatsApp ke pengguna\",\r\n" +
    "  \"action\": \"ask_information | wait_backend | ask_otp | verify_otp | done\"\r\n" +
    "}";


        public AICoreService(IHttpClientFactory httpClientFactory, IOptions<LlmOptions> options, ILogger<AICoreService> logger, IConfiguration configuration)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<string> SendMessage(string messages)
        {
            var (_, body) = await PostWhatsAppAsync(_configuration["WhatsApp:Receiver"], messages);
            return body;
        }

        // Kirim WhatsApp ke nomor tertentu (format 628xxxxxxxxxx). Mengembalikan false jika konfigurasi kosong atau
        // pengiriman gagal; isi pesan (mis. OTP) sengaja tidak dicatat di log.
        public async Task<bool> SendWhatsAppAsync(string receiver, string message)
        {
            if (string.IsNullOrWhiteSpace(_configuration["WhatsApp:UserCode"])
                || string.IsNullOrWhiteSpace(_configuration["WhatsApp:DeviceId"])
                || string.IsNullOrWhiteSpace(_configuration["WhatsApp:Secret"]))
            {
                _logger.LogError("WhatsApp belum dikonfigurasi (WhatsApp:UserCode, WhatsApp:DeviceId, WhatsApp:Secret)");
                return false;
            }

            try
            {
                var (ok, _) = await PostWhatsAppAsync(receiver, message);
                return ok;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogError(ex, "WhatsApp send failed");
                return false;
            }
        }

        private async Task<(bool Ok, string Body)> PostWhatsAppAsync(string? receiver, string message)
        {
            var payload = new
            {
                user_code = _configuration["WhatsApp:UserCode"],
                device_id = _configuration["WhatsApp:DeviceId"],
                receiver,
                message,
                secret = _configuration["WhatsApp:Secret"],
                //enableTypingEffect = false
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var client = _httpClientFactory.CreateClient();
            using var response = await client.PostAsync("https://api.kirimi.id/v1/send-message", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            var ok = response.IsSuccessStatusCode && !ReportsFailure(responseBody);
            if (!ok)
            {
                _logger.LogError("WhatsApp send failed with {StatusCode}: {Body}", (int)response.StatusCode, responseBody);
            }
            return (ok, responseBody);
        }

        // Sebagian gateway membalas HTTP 200 dengan {"success": false}; anggap itu gagal.
        private static bool ReportsFailure(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("success", out var success)
                    && success.ValueKind == JsonValueKind.False;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        //public async Task<string> GenerateResponse(string prompt, string sessionId, string? imagePath = null)
        //{
        //    var fullPrompt = $"[SYSTEM INSTRUCTION]\n{SystemPrompt}\n\n[USER MESSAGE]\n{prompt}";

        //    //var url = $"{Endpoint}?prompt={Uri.EscapeDataString(prompt)}&userId={Uri.EscapeDataString(sessionId)}";
        //    var url = $"{Endpoint}?prompt={Uri.EscapeDataString(fullPrompt)}&userId={Uri.EscapeDataString(sessionId)}";

        //    using var form = new MultipartFormDataContent();

        //    if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
        //    {
        //        var fileBytes = await File.ReadAllBytesAsync(imagePath);
        //        var fileContent = new ByteArrayContent(fileBytes);
        //        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        //        form.Add(fileContent, "file", Path.GetFileName(imagePath));
        //    }
        //    else
        //    {
        //        form.Add(new ByteArrayContent(Array.Empty<byte>()), "file", "empty.jpg");
        //    }

        //    var response = await _httpClient.PostAsync(url, form);
        //    response.EnsureSuccessStatusCode();

        //    var json = await response.Content.ReadAsStringAsync();
        //    var result = JsonSerializer.Deserialize<ChatCSResponse>(json, new JsonSerializerOptions
        //    {
        //        PropertyNameCaseInsensitive = true
        //    });

        //    return result?.Reply ?? "";
        //}

        public async Task<string> GenerateResponse(GenerateResponseRequest request, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrEmpty(request.ImagePath))
            {
                _logger.LogWarning("Image input is not supported by the LLM endpoint and was ignored");
            }

            PruneExpiredSessions();

            var session = string.IsNullOrWhiteSpace(request.SessionId)
                ? null
                : _sessions.GetOrAdd(request.SessionId, _ => new ChatSession());

            var userMessage = new ChatMessage("user", request.Prompt ?? "");
            var messages = new List<ChatMessage>();
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                messages.Add(new ChatMessage("system", request.SystemPrompt));
            }

            if (session == null)
            {
                messages.Add(userMessage);
                return await CompleteAsync(request.Model, messages, cancellationToken);
            }

            await session.Lock.WaitAsync(cancellationToken);
            try
            {
                messages.AddRange(session.Messages);
                messages.Add(userMessage);

                var reply = await CompleteAsync(request.Model, messages, cancellationToken);

                session.Messages.Add(userMessage);
                session.Messages.Add(new ChatMessage("assistant", reply));
                var excess = session.Messages.Count - Math.Max(_options.MaxHistoryMessages, 2);
                if (excess > 0)
                {
                    session.Messages.RemoveRange(0, excess + excess % 2);
                }
                session.LastUsed = DateTime.UtcNow;

                return reply;
            }
            finally
            {
                session.Lock.Release();
            }
        }

        public async Task<string> GenerateResponseMonitor(GenerateResponseRequest request, CancellationToken cancellationToken = default)
        {
            _logger.LogWarning($"Prompt: {request.Prompt}");

            var reply = await GenerateResponse(new GenerateResponseRequest
            {
                SystemPrompt = Prompting,
                Prompt = request.Prompt,
                Model = request.Model,
                ImagePath = request.ImagePath
            }, cancellationToken);

            _logger.LogWarning($"AI Reply: {reply}");

            await SendMessage(reply);

            return reply;
        }

        public void ClearSession(string sessionId)
        {
            _sessions.TryRemove(sessionId, out _);
        }

        private async Task<string> CompleteAsync(string? model, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                throw new InvalidOperationException("LLM:ApiKey is not configured");
            }

            var payload = new ChatCompletionRequest(
                string.IsNullOrWhiteSpace(model) ? _options.Model : model,
                messages,
                _options.Temperature,
                _options.MaxTokens,
                true);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
            };

            try
            {
                var client = _httpClientFactory.CreateClient(LlmClientName);
                using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token);
                    _logger.LogError("LLM request failed with {StatusCode}: {Body}", (int)response.StatusCode, Truncate(body));
                    throw new HttpRequestException($"LLM request failed with status {(int)response.StatusCode}", null, response.StatusCode);
                }

                var reply = response.Content.Headers.ContentType?.MediaType == "application/json"
                    ? await ReadJsonAsync(response, timeout.Token)
                    : await ReadStreamAsync(response, timeout.Token);

                if (string.IsNullOrWhiteSpace(reply))
                {
                    _logger.LogWarning("LLM returned an empty reply");
                }

                return reply;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"LLM request exceeded {_options.TimeoutSeconds} seconds");
            }
        }

        private async Task<string> ReadStreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            var content = new StringBuilder();
            string? finishReason = null;
            string? usage = null;

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    break;
                }

                using var chunk = JsonDocument.Parse(data);
                var root = chunk.RootElement;

                if (root.TryGetProperty("error", out var error))
                {
                    _logger.LogError("LLM stream error: {Error}", error.GetRawText());
                    throw new HttpRequestException("LLM stream returned an error");
                }

                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        content.Append(text.GetString());
                    }

                    if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    {
                        finishReason = reason.GetString();
                    }
                }

                if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
                {
                    usage = usageElement.GetRawText();
                }
            }

            LogCompletion(finishReason, usage);
            return content.ToString();
        }

        private async Task<string> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var choice = root.GetProperty("choices")[0];
            var finishReason = choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;
            var usage = root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object
                ? usageElement.GetRawText()
                : null;

            LogCompletion(finishReason, usage);
            return choice.GetProperty("message").GetProperty("content").GetString() ?? "";
        }

        private void LogCompletion(string? finishReason, string? usage)
        {
            if (finishReason == "length")
            {
                _logger.LogWarning("LLM reply was truncated by LLM:MaxTokens ({MaxTokens})", _options.MaxTokens);
            }

            if (usage != null)
            {
                _logger.LogInformation("LLM usage: {Usage}", usage);
            }
        }

        private void PruneExpiredSessions()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-_options.SessionTtlMinutes);
            foreach (var (key, session) in _sessions)
            {
                if (session.LastUsed < cutoff && session.Lock.CurrentCount == 1)
                {
                    _sessions.TryRemove(key, out _);
                }
            }
        }

        private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];

        private sealed class ChatSession
        {
            public List<ChatMessage> Messages { get; } = new();
            public SemaphoreSlim Lock { get; } = new(1, 1);
            public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        }

        private sealed record ChatMessage(
            [property: JsonPropertyName("role")] string Role,
            [property: JsonPropertyName("content")] string Content);

        private sealed record ChatCompletionRequest(
            [property: JsonPropertyName("model")] string Model,
            [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
            [property: JsonPropertyName("temperature")] double Temperature,
            [property: JsonPropertyName("max_tokens")] int MaxTokens,
            [property: JsonPropertyName("stream")] bool Stream);
    }
}
public class LlmOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 150;
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxHistoryMessages { get; set; } = 20;
    public int SessionTtlMinutes { get; set; } = 60;
}
public class GenerateResponseRequest
{
    public string Prompt { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? ImagePath { get; set; }
}
public class GenerateResponseRequestServer
{
    public string Prompt { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
}
