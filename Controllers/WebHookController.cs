using CS_AI.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using MQTTnet.Client;
using static System.Net.WebRequestMethods;
using System.Text.RegularExpressions;

namespace CS_AI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class WebHookController : ControllerBase
    {
        private readonly HttpClient _httpClient;
        private readonly AICoreService _aiService;
        private readonly MQTTService _mqttService;
        private readonly LDAPService _ldapService;
        private readonly ILogger<WebHookController> _logger;
        private IMqttClient _client;

        //private readonly LmStudioService _aiService;
        public WebHookController(HttpClient httpClient, AICoreService aiService, MQTTService mqttService, LDAPService ldapService, ILogger<WebHookController> logger)
        {
            _logger = logger;
            //_aiService = aiService;
            _httpClient = httpClient;
            _aiService = aiService;
            _mqttService = mqttService;
            _ldapService = ldapService;
        }

        [HttpPost]
        [Route("Receive")]
        public async Task<IActionResult> Receive([FromBody] WebhookWA data)
        {
            if (data == null)
            {
                return BadRequest();
            }
            // Balas 200 langsung (provider WA tidak menunggu proses AI), tapi jangan biarkan exception hilang tanpa log.
            _ = Task.Run(async () =>
            {
              try
              {
                switch (data.Event)
                {
                    case "message":
                        var results = "";
                        if (data.Name == "Caesar" && !string.IsNullOrWhiteSpace(data.Message))
                        {
                            if (data.Message == "check iis")
                            {
                                await _aiService.SendMessage("mohon tunggu saya sedang mengambil data server untuk Anda...");
                                var serverResult = await CheckServer();
                                Console.WriteLine(serverResult);
                                break;
                            }
                            if (data.Message.Contains("restart", StringComparison.OrdinalIgnoreCase))
                            {
                                var siteResult = await RestartSite(data.Message);
                                break;
                            }
                            if (data.Message == "check server")
                            {
                                var siteResult = await CheckServerUsage();
                                break;
                            }
                            var prompt = "Anda adalah Customer Service perusahaan melalui WhatsApp.\r\n" +
                                        "Tugas Anda hanya membantu proses reset password akun perusahaan.\r\n\r\n" +

                                        "GAYA BAHASA:\r\n" +
                                        "- Bahasa Indonesia sopan dan ramah.\r\n" +
                                        "- Kalimat singkat dan jelas (1–2 kalimat).\r\n" +
                                        "- Satu pertanyaan per pesan.\r\n" +
                                        "- Tidak kaku dan tidak terlalu formal.\r\n" +
                                        "- Tidak menggunakan emoji.\r\n\r\n" +

                                        "ATURAN:\r\n" +
                                        "- Fokus hanya pada proses reset password.\r\n" +
                                        "- Ikuti alur secara berurutan dan jangan melompat langkah.\r\n" +
                                        "- Jangan mengarang data.\r\n" +
                                        "- Jangan menjawab topik di luar reset password.\r\n" +
                                        "- Jangan pernah mengklaim mengirim OTP.\r\n" +
                                        "- Jika pengguna bertanya di luar proses, jawab: \"kami bantu proses reset password terlebih dahulu, ya.\"\r\n\r\n" +

                                        "ALUR RESET PASSWORD:\r\n" +
                                        "1. Minta username dan email terdaftar dalam satu pesan.\r\n" +
                                        "2. Setelah data diterima, ubah action menjadi \"wait_backend\", ambil usernamenya untuk diverifikasi di database dan balas \"Baik mohon ditunggu, akun anda sedang kami proses\".\r\n" +
                                        "3. Tunggu instruksi sistem (misalnya user mengirim OTP atau mengetik \"Anggap saja sudah lengkap\").\r\n" +
                                        "4. Jika user mengetik \"Anggap saja sudah lengkap\", langsung lanjut ke tahap meminta OTP dengan action \"ask_otp\".\r\n" +
                                        "5. Minta kode OTP dari pengguna.\r\n" +
                                        "6. Setelah OTP diterima, ubah action menjadi \"verify_otp\".\r\n" +
                                        "7. Setelah verifikasi OTP benar, kirim link reset: [https://adverif-dev.visionet.co.id/Account/ResetPassword](https://adverif-dev.visionet.co.id/Account/ResetPassword)\r\n" +
                                        "8. Jika menerima pesan system=\"system=True\", kirim pesan \"akun anda sudah berhasil di reset\".\r\n" +
                                        "9. Kirim konfirmasi hasil dan ubah action menjadi \"done\".\r\n\r\n" +

                                        "KETENTUAN SAPAAN:\r\n" +
                                        "Jika pengguna menyapa (halo, hai, pagi, dll.), balas: \"Selamat pagi/siang/sore/malam, saya adalah asisten yang akan membantu dalam hal berikut: 1. Ganti password 2. Bla bla 3. Bla bla\"\r\n\r\n" +

                                        "SELALU balas dengan format JSON:\r\n" +
                                        "{\r\n" +
                                        "  \"message\": \"pesan WhatsApp ke pengguna\",\r\n" +
                                        "  \"action\": \"ask_information | wait_backend | ask_otp | verify_otp | done\",\r\n" +
                                        "  \"data\": \"username:isi_username atau status_proses\"\r\n" +
                                        "}";

                            if (Regex.IsMatch(data.Message, @"^[0-9]+$", RegexOptions.IgnoreCase))
                            {
                                var OTP_Result = await VerifyOTP(data.Message);
                                data.Message = $"system=OTP {OTP_Result}";
                            }
                            var messages = new GenerateResponseRequest
                            {
                                SystemPrompt = prompt,
                                Prompt = $"message ={data.Message}",
                                SessionId = data.Name,
                                ImagePath = null
                            };

                            results = await _aiService.GenerateResponse(messages);
                            if (Regex.IsMatch(results, @"wait_backend", RegexOptions.IgnoreCase))
                            {
                                var username = "";
                                var match = Regex.Match(results, @"username:([^""\\]+)");
                                if (match.Success)
                                {
                                    username = match.Groups[1].Value;
                                }
                                if (username != null || username == "")
                                {
                                    try
                                    {
                                        var user = _ldapService.GetUser(username);

                                        if (user == null)
                                        {
                                            //return NotFound(new
                                            //{
                                            //    Found = false,
                                            //    Message = $"User '{username}' tidak ditemukan"
                                            //});
                                            break;
                                        }
                                        var OTP = GenerateOTP();
                                        await CreateOTP(OTP);

                                        var messages1 = new GenerateResponseRequest
                                        {
                                            SystemPrompt = prompt,
                                            Prompt = $"system:username terdaftar dan OTP = {OTP}",
                                            SessionId = data.Name,
                                            ImagePath = null
                                        };

                                        var system = await _aiService.GenerateResponse(messages1);
                                        var regexSystem = Regex.Match(system, @"\""message\""\s*:\s*\""([^\""]+)\""");
                                        if (regexSystem.Success)
                                        {
                                            await _aiService.SendMessage(regexSystem.Groups[1].Value);
                                        }
                                        //return Ok(new { Response = system });
                                        break;
                                    }
                                    catch (Exception ex)
                                    {
                                        //return StatusCode(500, new
                                        //{
                                        //    Found = false,
                                        //    Error = ex.InnerException
                                        //});
                                        break;
                                    }
                                }
                                else
                                {
                                    //return StatusCode(400, new { Response = "username tidak ada di database" });
                                    break;
                                }
                            }
                            //Console.WriteLine(results);
                            var regexMessage = Regex.Match(results, @"""message""\s*:\s*""([^""]+)""");

                            if (regexMessage.Success)
                            {
                                var sendMessage = regexMessage.Groups[1].Value;
                                //Console.WriteLine(sendMessage);
                                await _aiService.SendMessage(sendMessage);
                            }
                        }
                        break;

                    case "device.status":
                        Console.WriteLine("status event received");
                        break;

                    case "system.notification":
                        Console.WriteLine("notification received");
                        break;

                    default:
                        Console.WriteLine($"event: {data.Event}");
                        break;
                }
              }
              catch (Exception ex)
              {
                  _logger.LogError(ex, "Failed to process WhatsApp webhook event {Event}", data.Event);
              }
            });
            return Ok(new { status = "received" });
        }

        [NonAction]
        public async Task<string> CheckServer()
        {
            var data = await _mqttService.PublishAndWaitAsync("serverDev/iis/appsites/send", "testing");

            var prompt = $"kamu adalah AI yang bertugas untuk mengecheck dan memberikan informasi dari data yang saya kirim lalu berikan secara singkat, jelas,formatnya yang bagus, terstruktur dan buka dengan kalimat \"Dari data server yang saya ambil\". berikut datanya {data}";
            var message = new GenerateResponseRequest
            {
                Prompt = prompt,
                SessionId = "Session6",
                ImagePath = null
            };

            var result = await _aiService.GenerateResponse(message);
            await _aiService.SendMessage(result);
            return result;
        }
        [NonAction]
        public async Task<string> RestartSite(string payload)
        {
            var parts = payload.Split(':');

            //if (parts.Length < 2)
            //{
            //    return "Error";
            //}

            var site = parts[1].Trim();
            var restartResult = await _mqttService.PublishAndWaitAsync(
                "serverDev/iis/appsites/restart",
                $"{{\"name\":\"{site}\"}}"
            );
            if (restartResult.Contains("berhasil"))
            {
                await _aiService.SendMessage($"Entitas {site} berhasil direstart");
            }
            //var prompt = $"Sampaikan informasi hasil restart ini dengan ramah dan profesional: {restartResult}";
            //_logger.LogWarning(prompt);
            //var messages = new GenerateResponseRequest
            //{
            //    Prompt = prompt,
            //    SessionId = "Session6",
            //    Model = "qwen2.5vl:7b",
            //    ImagePath = null
            //};

            //var results = await _aiService.GenerateResponseMonitor(messages);
            //_logger.LogWarning("data =" + results);
            return "done";
        }
        [NonAction]
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

            await _aiService.SendMessage(message);

            return "done";
        }
        [NonAction]
        public string GenerateOTP()
        {
            int randomSixDigitNumber = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1000000);

            return randomSixDigitNumber.ToString("D6");
        }
        [NonAction]
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
        [NonAction]
        public async Task<bool> VerifyOTP(string OTP)
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "OTP.txt");

            if (!System.IO.File.Exists(filePath))
                return false;

            string otpDatabase = await System.IO.File.ReadAllTextAsync(filePath);
            otpDatabase = otpDatabase.Trim();

            return OTP.Trim() == otpDatabase;
        }
    }
}