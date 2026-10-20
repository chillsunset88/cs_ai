using CS_AI.Services;
using Microsoft.AspNetCore.Mvc;
using MQTTnet.Protocol;
using MQTTnet;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MQTTnet.Client;
using System.Threading.Channels;

namespace LMStudioApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AIController : ControllerBase
    {
        private IMqttClient _client;
        private readonly HttpClient _httpClient;
        //private readonly LmStudioService _aiService;
        private readonly AICoreService _aiCore;
        private readonly MQTTService _mqttService;
        private readonly IConfiguration _configuration;

        private TaskCompletionSource<string>? _tcs;

        public AIController(HttpClient httpClient, AICoreService aiCore, MQTTService mqttService, IConfiguration configuration)
        {
            _configuration = configuration;
            //_aiService = aiService;
            _httpClient = httpClient;
            _aiCore = aiCore;
            _mqttService = mqttService;
        }

        //[HttpPost("generate")]
        //public async Task<IActionResult> Generate(string request)
        //{
        //    if (string.IsNullOrWhiteSpace(request))
        //        return BadRequest("Prompt cannot be empty.");

        //    var result = await _aiService.GenerateTextAsync(request, "ses8");
        //    Console.WriteLine(result);
        //    SendMessage(result);
        //    return Ok(new { response = result });
        //}
        [HttpPost("sendmessage")]
        public void SendMessage(string messages)
        {
            var payload = new
            {
                user_code = _configuration["WhatsApp:UserCode"],
                device_id = _configuration["WhatsApp:DeviceId"],
                receiver = _configuration["WhatsApp:TestReceiver"],
                message = messages,
                secret = _configuration["WhatsApp:Secret"],
                enableTypingEffect = false
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = _httpClient.PostAsync("https://api.kirimi.id/v1/send-message", content);
            Console.WriteLine(response);
            var responseBody = response;

        }
        [HttpPost("testing")]
        public async Task<IActionResult> Generatetest(
            [FromForm] string prompt,
            [FromForm] string? userId = null,
            IFormFile? file = null)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return BadRequest("Prompt cannot be empty.");

            string? tempPath = null;
            if (file != null && file.Length > 0)
            {
                tempPath = Path.GetTempFileName();
                using var stream = new FileStream(tempPath, FileMode.Create);
                await file.CopyToAsync(stream);
            }

            try
            {
                var result = await _aiCore.GenerateResponseMonitor(new GenerateResponseRequest
                {
                    Prompt = prompt,
                    SessionId = userId,
                    ImagePath = tempPath
                }, HttpContext.RequestAborted);
                Console.WriteLine(result);
                return Ok(new { response = result });
            }
            finally
            {
                if (tempPath != null && System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
        }

        [HttpPost("PublishandWait")]
        public async Task<IActionResult> Testingin()
        {
            var result = await _mqttService.PublishAndWaitAsync("serverDev/iis/appsites/send", "testing");
            var prompt = $"kamu adalah Ai yang bertugas sebagai asisten untuk memberikan informasi tentang status server IIS (singkat saja, seperti informasi singkat) contoh hasil :berikut status aplikasi yang ada di server DEV, berikut:. Berikan informasi yang kamu dapatkan dari data berikut: {result}";
            return Ok(result);
        }
        //public async Task<IActionResult> Generatetest(
        //    [FromForm(Name = "Prompt")] string prompt,
        //    [FromForm(Name = "UserId")] string? userId,
        //    [FromForm(Name = "Model")] string? model,
        //    [FromForm(Name = "file")] IFormFile? file)
        //{
        //    if (string.IsNullOrWhiteSpace(prompt))
        //        return BadRequest("Prompt cannot be empty.");

        //    string? tempPath = null;

        //    if (file != null && file.Length > 0)
        //    {
        //        tempPath = Path.GetTempFileName();
        //        using var stream = new FileStream(tempPath, FileMode.Create);
        //        await file.CopyToAsync(stream);
        //    }

        //    try
        //    {
        //        var result = await _aiCore.GenerateResponse(new GenerateResponseRequest
        //        {
        //            Prompt = prompt,
        //            SessionId = userId,
        //            ImagePath = tempPath,
        //            Model = model ?? "qwen2.5vl:7b" // fallback
        //        });

        //        return Ok(new { response = result });
        //    }
        //    finally
        //    {
        //        if (tempPath != null && System.IO.File.Exists(tempPath))
        //            System.IO.File.Delete(tempPath);
        //    }
        //}
    }
}


public class MqttRequest
{
    public string CorrelationId { get; set; }
    public string Data { get; set; }
}

public class MqttResponse
{
    public string CorrelationId { get; set; }
    public string Data { get; set; }
}