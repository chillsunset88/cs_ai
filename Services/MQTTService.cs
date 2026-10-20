using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace CS_AI.Services
{
    public class MQTTService : BackgroundService
    {
        private IMqttClient _client;
        private readonly ILogger<MQTTService> _logger;
        private readonly AICoreService _aiCoreService;
        private readonly Channel<MqttResponse> _channel = Channel.CreateUnbounded<MqttResponse>();

        public MQTTService(ILogger<MQTTService> logger, AICoreService aICoreService)
        {
            _logger = logger;
            _aiCoreService = aICoreService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();

            var options = new MqttClientOptionsBuilder()
                .WithClientId($"api-{Environment.MachineName}")
                //.WithTcpServer("172.31.38.46", 1883)
                .WithTcpServer("10.254.210.105", 1883)
                .WithCleanSession()
                .Build();

            _client.ApplicationMessageReceivedAsync += async e =>
            {
                var topic = e.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload ?? []);

                _logger.LogWarning($"{topic} => {payload}");
                if (topic == "serverDev/iis/status")
                {
                    if (payload.Contains("message sudah aman"))
                    {
                        await _aiCoreService.SendMessage("Server sudah kembali normal");

                    }
                    else
                    {
                        var prompt = "Kamu adalah asisten server yang bertugas untuk menilai dan memberi saran tindakan yang harus dilakukan terhadap kondisi server. ini adalah kondisi servernya, outputnya tolong diberikan seperti kondisi terlebih dahulu dan saran tindakan. berikut kondisi server";
                        var request = new GenerateResponseRequest
                        {
                            SystemPrompt = prompt,
                            Prompt = payload,
                            SessionId = "Session7",
                            ImagePath = null
                        };
                        _logger.LogWarning($"request: {request.Prompt}");
                        var response = await _aiCoreService.GenerateResponse(request, stoppingToken);
                        await _aiCoreService.SendMessage(response);
                    _logger.LogWarning($"AI Response: {response}");
                    }
                }
                if (topic == "serverDev/iis/appsites/status")
                {
                    await _channel.Writer.WriteAsync(new MqttResponse
                    {
                        Data = payload
                    });

                }
                if (topic == "serverDev/status/result")
                {
                    await _channel.Writer.WriteAsync(new MqttResponse
                    {
                        Data = payload
                    });

                }
                //return Task.CompletedTask;
            };

            _client.ConnectedAsync += async e =>
            {
                _logger.LogWarning("MQTT Connected");

                await _client.SubscribeAsync("serverDev/iis/status");
                await _client.SubscribeAsync("serverDev/iis/appsites/status");
                await _client.SubscribeAsync("serverDev/status/result");

                _logger.LogInformation("Subscribed to device status");
            };

            _client.DisconnectedAsync += async e =>
            {
                _logger.LogWarning("MQTT Disconnected. Reconnecting...");
                await Task.Delay(3000, stoppingToken);

                try
                {
                    await _client.ConnectAsync(options, stoppingToken);
                }
                catch
                {
                    _logger.LogWarning("Reconnect failed");
                }
            };

            await _client.ConnectAsync(options, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        //public async Task<string> PublishAndWaitAsync(string data, int timeoutSeconds = 10)
        //{
        //    var correlationId = Guid.NewGuid().ToString();

        //    var request = new MqttRequest
        //    {
        //        CorrelationId = correlationId,
        //        Data = data
        //    };

        //    await _client.PublishAsync(new MqttApplicationMessageBuilder()
        //        .WithTopic("serverDev/iis/appsites/send")
        //        .WithPayload(JsonSerializer.Serialize(request))
        //    .Build());

        //    await foreach (var msg in _channel.Reader.ReadAllAsync())
        //    {
        //        if (msg.CorrelationId == correlationId)
        //            return msg.Data;
        //    }

        //    throw new Exception("No response");
        //}
        public async Task<string> PublishAndWaitAsync(string request, string payload, int timeoutSeconds = 5)
        {
            var requestTopic = request;

            _logger.LogWarning($"[MQTT SEND] {payload}");

            await _client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(requestTopic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(0)
                .Build());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                _logger.LogWarning("[MQTT] Waiting for response...");

                var msg = await _channel.Reader.ReadAsync(cts.Token);

                _logger.LogWarning($"[MQTT] Response received: {msg.Data}");

                return msg.Data;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("No response from serverDev/iis/appsites/status");
            }
        }
    }
}
