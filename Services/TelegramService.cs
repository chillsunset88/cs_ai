using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.DirectoryServices.AccountManagement;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace CS_AI.Services
{
    public class TelegramBotService : BackgroundService
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _configuration;
        private TelegramBotClient _bot;
        private readonly AICoreService _aiService;
        //private readonly LDAPService _ldapService;
        private readonly MQTTService _mqttService;
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly STTService _stt;
        private readonly TTSService _tts;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ConcurrentDictionary<long, PasswordResetState> _passwordResets = new();
        private readonly Dictionary<long, ChatQueue> _chatQueues = new();
        private readonly SemaphoreSlim _updateConcurrency;
        private readonly PasswordResetTokenService _resetTokens;
        private readonly string _ffmpegPath;

        private static readonly Regex ReplyMessageRegex = new(@"\""message\""\s*:\s*\""([^\""]+)\""", RegexOptions.Compiled);
        private static readonly Regex BillingLineRegex = new(@"^(\d+),\s*tagihan\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static volatile BillingSnapshot? _billingSnapshot;

        private static readonly char[] _charPool =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*".ToCharArray();

        private const string promptTextCS = "Anda adalah Customer Service perusahaan melalui Telegram.\r\n" +
                                        "Tugas Anda membantu:\r\n" +
                                        "1. Informasi billing\r\n" +
                                        "2. Laporan jalan rusak\r\n" +
                                        "3. Ganti atau reset password akun perusahaan\r\n\r\n" +

                                        "GAYA BAHASA:\r\n" +
                                        "- Gunakan Bahasa Indonesia sopan, ramah, dan natural.\r\n" +
                                        "- Kalimat singkat dan jelas (maksimal 2 kalimat).\r\n" +
                                        "- Maksimal 1 pertanyaan per pesan.\r\n" +
                                        "- Tidak terlalu formal dan tidak kaku.\r\n" +
                                        "- Tidak menggunakan emoji.\r\n\r\n" +

                                        "ATURAN UTAMA:\r\n" +
                                        "- Jangan mengarang data.\r\n" +
                                        "- Semua data billing dan status akun HARUS berasal dari system/backend.\r\n" +
                                        "- Ikuti alur sesuai intent user.\r\n" +
                                        "- Jangan mencampur flow antar fitur.\r\n" +
                                        "- Jangan pernah meminta, menyebutkan, atau mengarang password maupun kode OTP.\r\n" +
                                        "- Pesan bisa berasal dari transkripsi pesan suara sehingga ejaan mungkin kurang tepat; pahami maksud user secara toleran.\r\n" +
                                        "- Ganti password, reset password, lupa password, ganti sandi, atau lupa kata sandi = FLOW 3, walau diucapkan tidak baku.\r\n" +
                                        "- WAJIB membaca keyword \"system=\" sebagai instruksi backend.\r\n\r\n" +

                                        "KONDISI INPUT:\r\n" +
                                        "- Jika message diawali \"system=\" → itu respon backend.\r\n" +
                                        "- Selain itu → analisa intent user.\r\n\r\n" +

                                        "====================\r\n" +
                                        "FLOW 1: BILLING\r\n" +
                                        "====================\r\n" +
                                        "1. Jika user bertanya tentang billing atau tagihan:\r\n" +
                                        "   action=\"ask_billing_number\"\r\n" +
                                        "   message=\"Mohon kirim nomor billing Anda.\"\r\n" +
                                        "2. Jika user mengirim angka setelah diminta nomor billing atau setelah informasi tagihan diberikan:\r\n" +
                                        "   action=\"wait_backend\"\r\n" +
                                        "   data=\"billing_number:isi_nomor\"\r\n" +
                                        "   message=\"Baik, mohon ditunggu. Data billing Anda sedang kami cek.\"\r\n" +
                                        "3. Jika menerima system=billing_found:<nominal>:\r\n" +
                                        "   action=\"billing_info\"\r\n" +
                                        "   message=\"Tagihan Anda sebesar Rp <nominal>.\"\r\n" +
                                        "4. Jika menerima system=billing_not_found:\r\n" +
                                        "   action=\"ask_billing_number\"\r\n" +
                                        "   message=\"Nomor billing tidak ditemukan. Mohon kirim ulang nomor billing Anda.\"\r\n\r\n" +

                                        "====================\r\n" +
                                        "FLOW 2: JALAN RUSAK\r\n" +
                                        "====================\r\n" +
                                        "1. Jika user melaporkan jalan rusak tanpa alamat:\r\n" +
                                        "   action=\"ask_address\"\r\n" +
                                        "   message=\"Mohon kirim alamat lokasi jalan rusak tersebut.\"\r\n" +
                                        "2. Jika user mengirim alamat jalan rusak:\r\n" +
                                        "   action=\"create_ticket\"\r\n" +
                                        "   data=\"address:isi_alamat\"\r\n" +
                                        "   message=\"Laporan berhasil dibuat.\"\r\n\r\n" +

                                        "====================\r\n" +
                                        "FLOW 3: GANTI / RESET PASSWORD\r\n" +
                                        "====================\r\n" +
                                        "1. Jika user ingin ganti password, reset password, atau lupa password:\r\n" +
                                        "   action=\"ask_username\"\r\n" +
                                        "   message=\"Mohon kirim username akun perusahaan Anda.\"\r\n" +
                                        "2. Jika sebelumnya meminta username dan user mengirim username:\r\n" +
                                        "   action=\"verify_user\"\r\n" +
                                        "   data=\"username:isi_username\"\r\n" +
                                        "   message=\"Baik, mohon ditunggu. Akun Anda sedang kami cek.\"\r\n" +
                                        "3. Jika menerima system=user_not_found:\r\n" +
                                        "   action=\"ask_username\"\r\n" +
                                        "   message=\"Username tidak ditemukan. Mohon kirim ulang username Anda.\"\r\n" +
                                        "4. Jika menerima system=otp_sent:\r\n" +
                                        "   action=\"ask_otp\"\r\n" +
                                        "   message=\"Kode OTP sudah kami kirim. Silakan balas dengan kode OTP tersebut.\"\r\n" +
                                        "5. Jika menerima system=otp_invalid:\r\n" +
                                        "   action=\"ask_otp\"\r\n" +
                                        "   message=\"Kode OTP tidak valid. Silakan kirim ulang kode OTP Anda.\"\r\n" +
                                        "6. Jika menerima system=otp_expired:\r\n" +
                                        "   action=\"ask_username\"\r\n" +
                                        "   message=\"Kode OTP sudah tidak berlaku. Mohon kirim ulang username Anda.\"\r\n" +
                                        "7. Jika menerima system=otp_verified:\r\n" +
                                        "   action=\"done\"\r\n" +
                                        "   message=\"Kode OTP benar. Silakan buka link yang kami kirim untuk membuat password baru.\"\r\n\r\n" +

                                        "====================\r\n" +
                                        "FALLBACK\r\n" +
                                        "====================\r\n" +
                                        "Jika tidak dikenali atau pengguna menyapa:\r\n" +
                                        "   action=\"ask_information\"\r\n" +
                                        "   message=perkenalkan layanan (billing, laporan jalan rusak, ganti password) dan tanyakan kebutuhan pengguna\r\n\r\n" +

                                        "FORMAT OUTPUT JSON:\r\n" +
                                        "{\r\n" +
                                        "  \"message\": \"pesan ke user\",\r\n" +
                                        "  \"action\": \"ask_information | ask_billing_number | wait_backend | billing_info | ask_address | create_ticket | ask_username | verify_user | ask_otp | done\",\r\n" +
                                        "  \"data\": \"billing_number:xxx | address:xxx | username:xxx | kosong\"\r\n" +
                                        "}\r\n\r\n" +

                                        "JANGAN PERNAH menambahkan teks di luar JSON.";

        public TelegramBotService(ILogger<TelegramBotService> logger, IConfiguration configuration, AICoreService aiService, MQTTService mqttService, HttpClient httpClient, IWebHostEnvironment env, STTService stt, TTSService tts, IServiceScopeFactory scopeFactory, PasswordResetTokenService resetTokens)
        {
            _scopeFactory = scopeFactory;
            _resetTokens = resetTokens;
            _configuration = configuration;
            _aiService = aiService;
            //_ldapService = ldapService;
            //_mqttService = mqttService;
            _httpClient = httpClient;
            _logger = logger;
            _env = env;
            _ffmpegPath = FfmpegLocator.Resolve(configuration, env.WebRootPath);
            _stt = stt;
            _tts = tts;
            _updateConcurrency = new SemaphoreSlim(Math.Max(1, configuration.GetValue<int?>("Telegram:MaxConcurrentUpdates") ?? 8));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var token = _configuration["Telegram:BotToken"];
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogError("Telegram:BotToken is not configured, Telegram bot is disabled");
                return;
            }

            await CheckFfmpegAsync(stoppingToken);

            _bot = new TelegramBotClient(token);

            while (true)
            {
                try
                {
                    var me = await _bot.GetMe(stoppingToken);
                    Console.WriteLine($"Bot Started : {me.Username}");
                    break;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError("Telegram bot cannot reach Telegram API, retrying in 30 seconds: {Error}", ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }

            _bot.StartReceiving(
                 updateHandler: HandleUpdateAsync,
                 errorHandler: HandleErrorAsync,
                 receiverOptions: new ReceiverOptions
                 {
                     // Handler hanya memakai update.Message; jenis update lain tidak perlu diunduh.
                     AllowedUpdates = new[] { UpdateType.Message }
                 },
                 cancellationToken: stoppingToken
             );

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        // Telegram.Bot memanggil handler satu per satu, sehingga satu proses lambat (LLM/STT/TTS) menahan semua user lain.
        // Update karena itu diproses di luar loop polling. Urutan per chat tetap dijaga (ChatQueue), jadi alur sesi
        // billing / jalan rusak / reset password tidak pernah tumpang tindih; chat berbeda berjalan paralel.
        private Task HandleUpdateAsync(
            ITelegramBotClient botClient,
            Update update,
            CancellationToken cancellationToken)
        {
            var message = update.Message;
            if (message == null)
                return Task.CompletedTask;

            EnqueueForChat(message.Chat.Id, () => ProcessUpdateAsync(botClient, update, cancellationToken), cancellationToken);
            return Task.CompletedTask;
        }

        private void EnqueueForChat(long chatId, Func<Task> work, CancellationToken cancellationToken)
        {
            lock (_chatQueues)
            {
                if (!_chatQueues.TryGetValue(chatId, out var queue))
                {
                    queue = new ChatQueue();
                    _chatQueues[chatId] = queue;
                }

                queue.Pending++;
                queue.Tail = RunAfterAsync(queue, queue.Tail, chatId, work, cancellationToken);
            }
        }

        private async Task RunAfterAsync(ChatQueue queue, Task previous, long chatId, Func<Task> work, CancellationToken cancellationToken)
        {
            // Yield memastikan kelanjutan tidak berjalan di dalam lock milik EnqueueForChat.
            await Task.Yield();
            await previous;

            try
            {
                await _updateConcurrency.WaitAsync(cancellationToken);
                try
                {
                    await work();
                }
                finally
                {
                    _updateConcurrency.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error while processing Telegram update for chat {ChatId}", chatId);
            }
            finally
            {
                lock (_chatQueues)
                {
                    if (--queue.Pending == 0)
                    {
                        _chatQueues.Remove(chatId);
                    }
                }
            }
        }

        private async Task ProcessUpdateAsync(
            ITelegramBotClient botClient,
            Update update,
            CancellationToken cancellationToken)
        {
            //if (update.Type != UpdateType.Message)
            //    return;

            //if (update.Message is null)
            //    return;

            var message = update.Message;
            if (message == null)
                return;

            var chatId = message.Chat.Id;
            var text = message.Text;
            var phoneNumber = message.Contact?.PhoneNumber;

            //if (string.IsNullOrEmpty(text))
            //    return;

            if (message.Voice != null)
            {
                await HandleVoiceAsync(botClient, chatId, message.Voice.FileId, cancellationToken);
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
                return;

            switch (text.ToLower())
            {
                case "/start":
                    await botClient.SendMessage(chatId, $"Hello");
                    break;

                case "/time":
                    await botClient.SendMessage(chatId, DateTime.Now.ToString("dd MMM yyyy HH:mm:ss"));
                    break;

                case "/checkserver":
                    await botClient.SendMessage(chatId, "Server Running Normal");
                    break;
                case "/reset":
                    _aiService.ClearSession(chatId.ToString());
                    _passwordResets.TryRemove(chatId, out _);
                    break;

                default:
                    await HandleTextAsync(botClient, chatId, text, cancellationToken);
                    break;

                    //default:
                    //    //var prompting = prompt + "\r\n\r\n" + "Pesan dari pengguna: " + text;
                    //    //var messages = new GenerateResponseRequest
                    //    //{
                    //    //    Prompt = prompting,
                    //    //    SessionId = chatId.ToString(),
                    //    //    Model = "qwen2.5vl:7b",
                    //    //    ImagePath = null
                    //    //};

                    //    //var results = await _aiService.GenerateResponse(messages);
                    //    //Console.WriteLine(results);
                    //    //var regexMessage = Regex.Match(results, @"""message""\s*:\s*""([^""]+)""");
                    //    //var sendMessage = regexMessage.Groups[1].Value;
                    //    //Console.WriteLine(sendMessage);
                    //    if (Regex.IsMatch(text, @"^[0-9]+$", RegexOptions.IgnoreCase))
                    //    {
                    //        var OTP_Result = await VerifyOTP(text);
                    //        var valid = "otp_invalid";
                    //        Console.WriteLine($"OTP={OTP_Result}");
                    //        if (OTP_Result == true)
                    //        {
                    //            valid = "otp_valid";
                    //        }
                    //        text = $"system=OTP {valid}";
                    //        var Password = GeneratePassword();
                    //        await botClient.SendMessage(chatId, $"Password baru Anda adalah {Password}");
                    //    }
                    //    var prompting = $"{prompt}\nmessage={text}\ndatetime = {DateTime.Now}";
                    //    var messages = new GenerateResponseRequest
                    //    {
                    //        Prompt = prompting,
                    //        SessionId = chatId.ToString(),
                    //        Model = "qwen2.5vl:7b",
                    //        ImagePath = null
                    //    };

                    //    var results = await _aiService.GenerateResponse(messages);
                    //    Console.WriteLine("result AI");
                    //    Console.WriteLine(results);
                    //    if (Regex.IsMatch(results, @"wait_backend", RegexOptions.IgnoreCase))
                    //    {
                    //        await botClient.SendMessage(chatId, "Baik, mohon ditunggu. Akun Anda sedang kami proses.");
                    //        var username = "";
                    //        //var match = Regex.Match(results, @"username:([^""\\]+)");
                    //        var match = Regex.Match(results, @"""data""\s*:\s*""([^""]+)""");
                    //        if (match.Success)
                    //        {
                    //            _logger.LogWarning("berhasil regex");
                    //            username = match.Groups[1].Value;
                    //            _logger.LogWarning($"username = {username}");
                    //        }
                    //        if (username != null || username != "")
                    //        {
                    //            try
                    //            {
                    //                _logger.LogWarning("mencari user di LDAP");
                    //                //var user = _ldapService.GetUser(username);
                    //                var user = "halo";

                    //                _logger.LogWarning($"user = {user}");
                    //                if (user == null)
                    //                {
                    //                    await botClient.SendMessage(chatId, $"User '{username}' tidak ditemukan");
                    //                    //return NotFound(new
                    //                    //{
                    //                    //    Found = false,
                    //                    //    Message = $"User '{username}' tidak ditemukan"
                    //                    //});
                    //                    break;
                    //                }
                    //                var OTP = GenerateOTP();
                    //                await CreateOTP(OTP);
                    //                await botClient.SendMessage(chatId, $"OTP anda {OTP}");
                    //                var prompting1 = $"{prompt}\nsystem=user_valid\nOTP = {OTP}";
                    //                var messages1 = new GenerateResponseRequest
                    //                {
                    //                    Prompt = prompting1,
                    //                    SessionId = chatId.ToString(),
                    //                    Model = "qwen2.5vl:7b",
                    //                    ImagePath = null
                    //                };

                    //                var system = await _aiService.GenerateResponse(messages1);
                    //                var regexSystem = Regex.Match(system, @"\""message\""\s*:\s*\""([^\""]+)\""");
                    //                if (regexSystem.Success)
                    //                {
                    //                    await botClient.SendMessage(chatId, regexSystem.Groups[1].Value);
                    //                }

                    //                //return Ok(new { Response = system });
                    //                break;
                    //            }
                    //            catch (Exception ex)
                    //            {
                    //                //return StatusCode(500, new
                    //                //{
                    //                //    Found = false,
                    //                //    Error = ex.InnerException
                    //                //});
                    //                break;
                    //            }
                    //        }
                    //        else
                    //        {
                    //            //return StatusCode(400, new { Response = "username tidak ada di database" });
                    //            break;
                    //        }
                    //    }
                    //    //Console.WriteLine(results);
                    //    var regexMessage = Regex.Match(results, @"""message""\s*:\s*""([^""]+)""");
                    //    var sendMessage = regexMessage.Groups[1].Value;
                    //    await botClient.SendMessage(chatId, sendMessage);

                    //    if (Regex.IsMatch(results, @"done", RegexOptions.IgnoreCase))
                    //    {
                    //        var response = await _httpClient.PostAsync($"http://13.250.212.143:5000/api/AI/deleteSession?chatId={chatId}", null);
                    //        response.EnsureSuccessStatusCode();

                    //        var json = await response.Content.ReadAsStringAsync();

                    //        var result = JsonSerializer.Deserialize<ChatCSResponse>(json, new JsonSerializerOptions
                    //        {
                    //            PropertyNameCaseInsensitive = true
                    //        });
                    //    }
                    //    break;
            }
        }

        // Pesan suara ditranskripsi lalu diproses dengan alur yang SAMA seperti pesan teks
        // (billing, jalan rusak, ganti/reset password); hanya balasannya yang dikirim sebagai suara.
        private async Task HandleVoiceAsync(ITelegramBotClient botClient, long chatId, string fileId, CancellationToken cancellationToken)
        {
            var totalTimer = Stopwatch.StartNew();
            string? oggPath = null;
            try
            {
                var downloadTimer = Stopwatch.StartNew();
                try
                {
                    oggPath = await DownloadVoiceAsync(botClient, fileId, cancellationToken);
                }
                finally
                {
                    _logger.LogInformation("Audio stage {Stage} completed in {DurationMs} ms for chat {ChatId}", "telegram_download", downloadTimer.ElapsedMilliseconds, chatId);
                }

                string transcript;
                try
                {
                    transcript = (await ProcessVoiceToText(oggPath, cancellationToken)).Trim();
                }
                finally
                {
                    TTSService.TryDelete(oggPath);
                    TTSService.TryDelete(Path.ChangeExtension(oggPath, ".wav"));
                }

                // Transkrip bisa memuat OTP/username, jadi hanya dicatat pada level Debug.
                _logger.LogDebug("Voice transcript for chat {ChatId}: {Transcript}", chatId, transcript);

                if (string.IsNullOrWhiteSpace(transcript))
                {
                    await ReplyAsync(botClient, chatId, "Maaf, suara Anda kurang jelas. Silakan ulangi atau kirim pesan teks.", true, cancellationToken);
                    return;
                }

                await HandleTextAsync(botClient, chatId, transcript, cancellationToken, viaVoice: true);
            }
            finally
            {
                _logger.LogInformation("Audio pipeline finished in {DurationMs} ms for chat {ChatId}", totalTimer.ElapsedMilliseconds, chatId);
            }
        }

        private async Task SendSpeechAsync(ITelegramBotClient botClient, long chatId, string text, CancellationToken cancellationToken)
        {
            var ttsTimer = Stopwatch.StartNew();
            string audioPath;
            try
            {
                audioPath = await _tts.GenerateSpeechAsync(text);
            }
            finally
            {
                _logger.LogInformation("Audio stage {Stage} completed in {DurationMs} ms for chat {ChatId}", "tts_total", ttsTimer.ElapsedMilliseconds, chatId);
            }

            try
            {
                var uploadTimer = Stopwatch.StartNew();
                try
                {
                    await using var stream = System.IO.File.OpenRead(audioPath);
                    await botClient.SendAudio(
                        chatId,
                        InputFile.FromStream(stream),
                        cancellationToken: cancellationToken
                    );
                }
                finally
                {
                    _logger.LogInformation("Audio stage {Stage} completed in {DurationMs} ms for chat {ChatId}", "telegram_audio_upload", uploadTimer.ElapsedMilliseconds, chatId);
                }
            }
            finally
            {
                TTSService.TryDelete(audioPath);
            }
        }

        // Balasan suara bila pesan asalnya suara; jika TTS gagal, jatuh ke teks agar user tetap dapat jawaban.
        private async Task ReplyAsync(ITelegramBotClient botClient, long chatId, string message, bool viaVoice, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (viaVoice)
            {
                try
                {
                    await SendSpeechAsync(botClient, chatId, message, cancellationToken);
                    return;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "TTS failed for chat {ChatId}, falling back to text", chatId);
                }
            }

            await SendTextAsync(botClient, chatId, message, cancellationToken);
        }

        private Task HandleErrorAsync(
            ITelegramBotClient botClient,
            Exception exception,
            CancellationToken cancellationToken)
        {
            Console.WriteLine(exception.Message);
            return Task.CompletedTask;
        }

        private async Task HandleTextAsync(ITelegramBotClient botClient, long chatId, string text, CancellationToken cancellationToken, bool viaVoice = false)
        {
            try
            {
                var input = text.Trim();
                // Dari suara, OTP bisa terbaca "123 456" atau "kode saya 123456."; ambil digitnya saja.
                var otpCandidate = viaVoice ? Regex.Replace(input, @"\D", "") : input;
                if (_passwordResets.TryGetValue(chatId, out var pending) && Regex.IsMatch(otpCandidate, @"^\d{6}$"))
                {
                    await VerifyPasswordOtpAsync(botClient, chatId, pending, otpCandidate, cancellationToken, viaVoice);
                    return;
                }

                var reply = await AskTextAsync(chatId, $"message={input}\ndatetime = {DateTime.Now}", cancellationToken);

                switch (reply.Action)
                {
                    case "wait_backend":
                        await ReplyAsync(botClient, chatId, reply.Message, viaVoice, cancellationToken);
                        var amount = await FindBillingAmountAsync(DataValue(reply.Data, "billing_number"));
                        var billingStatus = amount == null ? "system=billing_not_found" : $"system=billing_found:{amount}";
                        var billingReply = await AskTextAsync(chatId, billingStatus, cancellationToken);
                        await ReplyAsync(botClient, chatId, billingReply.Message, viaVoice, cancellationToken);
                        break;

                    case "create_ticket":
                        var ticketNumber = RandomNumberGenerator.GetInt32(100000, 999999);
                        await ReplyAsync(botClient, chatId, $"{reply.Message} Nomor tiket Anda: {ticketNumber}.", viaVoice, cancellationToken);
                        _aiService.ClearSession(chatId.ToString());
                        break;

                    case "verify_user":
                        await ReplyAsync(botClient, chatId, reply.Message, viaVoice, cancellationToken);
                        await StartPasswordResetAsync(botClient, chatId, DataValue(reply.Data, "username"), cancellationToken, viaVoice);
                        break;

                    case "done":
                        await ReplyAsync(botClient, chatId, reply.Message, viaVoice, cancellationToken);
                        _aiService.ClearSession(chatId.ToString());
                        break;

                    default:
                        await ReplyAsync(botClient, chatId, reply.Message, viaVoice, cancellationToken);
                        break;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Failed to handle Telegram text message for chat {ChatId}", chatId);
                await botClient.SendMessage(chatId, "Maaf, terjadi kendala saat memproses permintaan Anda. Silakan coba lagi.", cancellationToken: cancellationToken);
            }
        }

        private async Task StartPasswordResetAsync(ITelegramBotClient botClient, long chatId, string username, CancellationToken cancellationToken, bool viaVoice = false)
        {
            var found = !string.IsNullOrWhiteSpace(username) && await Task.Run(() =>
            {
                using var scope = _scopeFactory.CreateScope();
                return scope.ServiceProvider.GetRequiredService<LDAPService>().TelegramUserExists(username);
            }, cancellationToken);

            if (!found)
            {
                await ContinuePasswordFlowAsync(botClient, chatId, "system=user_not_found", cancellationToken, viaVoice);
                return;
            }

            var otp = GenerateOTP();
            _passwordResets[chatId] = new PasswordResetState(username, otp, DateTime.UtcNow.AddMinutes(5));
            await botClient.SendMessage(chatId, $"Kode OTP Anda: {otp}\nBerlaku 5 menit.", cancellationToken: cancellationToken);
            await ContinuePasswordFlowAsync(botClient, chatId, "system=otp_sent", cancellationToken, viaVoice);
        }

        private async Task VerifyPasswordOtpAsync(ITelegramBotClient botClient, long chatId, PasswordResetState pending, string otp, CancellationToken cancellationToken, bool viaVoice = false)
        {
            if (pending.ExpiresAt < DateTime.UtcNow || pending.Attempts >= 3)
            {
                _passwordResets.TryRemove(chatId, out _);
                await ContinuePasswordFlowAsync(botClient, chatId, "system=otp_expired", cancellationToken, viaVoice);
                return;
            }

            if (otp != pending.Otp)
            {
                pending.Attempts++;
                await ContinuePasswordFlowAsync(botClient, chatId, "system=otp_invalid", cancellationToken, viaVoice);
                return;
            }

            _passwordResets.TryRemove(chatId, out _);

            // Password baru dibuat user sendiri lewat halaman web; token terikat ke username yang OTP-nya sudah benar.
            var token = _resetTokens.Create(pending.Username);
            var baseUrl = (_configuration["PasswordReset:BaseUrl"] ?? "http://localhost:5000").TrimEnd('/');
            var link = $"{baseUrl}/account/resetpassword?token={token}";

            _logger.LogWarning("Password reset link issued for AD user {Username} via Telegram chat {ChatId}", pending.Username, chatId);

            // Link selalu dikirim sebagai teks (tidak pernah dibacakan lewat suara).
            await botClient.SendMessage(
                chatId,
                $"Buka link berikut untuk membuat password baru (sekali pakai, berlaku {(int)_resetTokens.Lifetime.TotalMinutes} menit):\n{link}",
                cancellationToken: cancellationToken);

            await ContinuePasswordFlowAsync(botClient, chatId, "system=otp_verified", cancellationToken, viaVoice);
        }

        private async Task ContinuePasswordFlowAsync(ITelegramBotClient botClient, long chatId, string systemMessage, CancellationToken cancellationToken, bool viaVoice = false)
        {
            var reply = await AskTextAsync(chatId, systemMessage, cancellationToken);
            await ReplyAsync(botClient, chatId, reply.Message, viaVoice, cancellationToken);
            if (reply.Action == "done")
            {
                _aiService.ClearSession(chatId.ToString());
            }
        }

        private async Task<(string Message, string Action, string Data)> AskTextAsync(long chatId, string prompt, CancellationToken cancellationToken)
        {
            var llmTimer = Stopwatch.StartNew();
            try
            {
                var result = await _aiService.GenerateResponse(new GenerateResponseRequest
                {
                    SystemPrompt = promptTextCS,
                    Prompt = prompt,
                    SessionId = chatId.ToString()
                }, cancellationToken);

                return ParseReply(result);
            }
            finally
            {
                _logger.LogInformation("Audio stage {Stage} completed in {DurationMs} ms for chat {ChatId}", "llm", llmTimer.ElapsedMilliseconds, chatId);
            }
        }

        private static (string Message, string Action, string Data) ParseReply(string reply)
        {
            var match = Regex.Match(reply, @"\{.*\}", RegexOptions.Singleline);
            if (match.Success)
            {
                try
                {
                    using var doc = JsonDocument.Parse(match.Value);
                    var root = doc.RootElement;
                    return (ReadString(root, "message"), ReadString(root, "action"), ReadString(root, "data"));
                }
                catch (JsonException)
                {
                }
            }

            return (reply.Trim(), "", "");
        }

        private static string ReadString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }

        private static string DataValue(string data, string key)
        {
            var prefix = key + ":";
            return data.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? data[prefix.Length..].Trim()
                : data.Trim();
        }

        private static async Task SendTextAsync(ITelegramBotClient botClient, long chatId, string message, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                await botClient.SendMessage(chatId, message, cancellationToken: cancellationToken);
            }
        }

        // database.txt dibaca ulang hanya jika file berubah, bukan setiap pertanyaan billing.
        private static async Task<string?> FindBillingAmountAsync(string billingNumber)
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "database.txt");
            if (string.IsNullOrWhiteSpace(billingNumber) || !System.IO.File.Exists(filePath))
                return null;

            var stamp = System.IO.File.GetLastWriteTimeUtc(filePath);
            var snapshot = _billingSnapshot;
            if (snapshot == null || snapshot.Stamp != stamp)
            {
                var amounts = new Dictionary<string, string>();
                foreach (var line in await System.IO.File.ReadAllLinesAsync(filePath))
                {
                    var match = BillingLineRegex.Match(line);
                    if (match.Success)
                    {
                        // Baris pertama untuk nomor yang sama yang dipakai, sama seperti sebelumnya.
                        amounts.TryAdd(match.Groups[1].Value, match.Groups[2].Value);
                    }
                }

                snapshot = new BillingSnapshot(stamp, amounts);
                _billingSnapshot = snapshot;
            }

            return snapshot.Amounts.TryGetValue(billingNumber, out var amount) ? amount : null;
        }

        private sealed record BillingSnapshot(DateTime Stamp, Dictionary<string, string> Amounts);

        private sealed class ChatQueue
        {
            public Task Tail { get; set; } = Task.CompletedTask;
            public int Pending { get; set; }
        }

        private sealed class PasswordResetState
        {
            public PasswordResetState(string username, string otp, DateTime expiresAt)
            {
                Username = username;
                Otp = otp;
                ExpiresAt = expiresAt;
            }

            public string Username { get; }
            public string Otp { get; }
            public DateTime ExpiresAt { get; }
            public int Attempts { get; set; }
        }

        public async Task<string> CheckServerUsage()
        {
            var result = await _mqttService.PublishAndWaitAsync(
                "serverDev/status",
                $"{{\"name\":\"testing\"}}"
            );
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;

            var cpu = root.GetProperty("CPU").GetString();
            var usedRam = root.GetProperty("UsedRAM").GetString();
            var freeRam = root.GetProperty("FreeRAM").GetString();

            var message = $"Penggunaan resource di server saat ini adalah CPU sebesar {cpu}, " +
                    $"dengan penggunaan RAM {usedRam} dan sisa RAM {freeRam}.";

            _aiService.SendMessage(message);

            return "done";
        }
        public string GenerateOTP()
        {
            int randomSixDigitNumber = RandomNumberGenerator.GetInt32(0, 1000000);

            string formattedNumber = randomSixDigitNumber.ToString("D6");

            return formattedNumber;
        }
        public async Task<bool> CreateOTP(string OTP)
        {
            try
            {
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "OTP.txt");

                await System.IO.File.WriteAllTextAsync(filePath, OTP);

                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }
        public async Task<bool> VerifyOTP(string OTP)
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "OTP.txt");

            if (!System.IO.File.Exists(filePath))
                return false;

            string otpDatabase = await System.IO.File.ReadAllTextAsync(filePath);
            otpDatabase = otpDatabase.Trim();

            return OTP.Trim() == otpDatabase;
        }

        public string GeneratePassword(int length = 10)
        {
            if (length > _charPool.Length)
                throw new ArgumentException("Length exceeds unique character pool.");

            var passwordChars = new List<char>
            {
                RandomNumberGenerator.GetItems<char>("ABCDEFGHIJKLMNOPQRSTUVWXYZ", 1)[0],
                RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 1)[0],
                RandomNumberGenerator.GetItems<char>("0123456789", 1)[0],
                RandomNumberGenerator.GetItems<char>("!@#$%^&*", 1)[0]
            };

            while (passwordChars.Count < length)
            {
                var c = _charPool[RandomNumberGenerator.GetInt32(_charPool.Length)];

                if (!passwordChars.Contains(c))
                {
                    passwordChars.Add(c);
                }
            }

            var result = passwordChars.ToArray();
            RandomNumberGenerator.Shuffle<char>(result);
            return new string(result);
        }

        public async Task<string> DownloadVoiceAsync(ITelegramBotClient bot, string fileId, CancellationToken cancellationToken = default)
        {
            var file = await bot.GetFile(fileId, cancellationToken);

            var filePath = Path.Combine(_env.WebRootPath, "voices", $"{fileId}.ogg");

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            await using (var fs = new FileStream(filePath, FileMode.Create))
            {
                await bot.DownloadFile(file.FilePath!, fs, cancellationToken);
            }

            return filePath;
        }

        // Voice note (STT) dan balasan suara (TTS) butuh ffmpeg; beri peringatan jelas saat startup bila belum terpasang.
        private async Task CheckFfmpegAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = "-version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);

                _logger.LogInformation("ffmpeg ready: {FfmpegPath}", _ffmpegPath);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("ffmpeg tidak bisa dijalankan ({FfmpegPath}): {Error}. Voice note dan balasan suara akan gagal. " +
                                 "Install ffmpeg di server (pastikan 'ffmpeg -version' jalan) atau isi Ffmpeg:Path.", _ffmpegPath, ex.Message);
            }
        }

        public async Task<string> ConvertToWavAsync(string inputPath, CancellationToken cancellationToken = default)
        {
            var outputPath = Path.ChangeExtension(inputPath, ".wav");
            var ffmpegPath = _ffmpegPath;

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -i \"{inputPath}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{outputPath}\"",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            try
            {
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                var error = await errorTask;

                if (!System.IO.File.Exists(outputPath))
                    throw new Exception($"FFmpeg failed: {error}");
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                throw;
            }

            return outputPath;
        }

        public async Task<string> ProcessVoiceToText(string oggPath, CancellationToken cancellationToken = default)
        {
            var conversionTimer = Stopwatch.StartNew();
            string wavPath;
            try
            {
                wavPath = await ConvertToWavAsync(oggPath, cancellationToken);
            }
            finally
            {
                _logger.LogInformation("Audio stage {Stage} completed in {DurationMs} ms", "ffmpeg_input_conversion", conversionTimer.ElapsedMilliseconds);
            }

            var sttTimer = Stopwatch.StartNew();
            try
            {
                return await _stt.SpeechToText(wavPath, cancellationToken);
            }
            finally
            {
                _logger.LogInformation("Audio stage {Stage} completed in {DurationMs} ms", "whisper_stt", sttTimer.ElapsedMilliseconds);
            }
        }

    }
}