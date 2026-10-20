using Google.GenAI;
using Google.GenAI.Types;
using System.Diagnostics;

namespace CS_AI.Services
{
    public class TTSService
    {
        private readonly Client _client;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<TTSService> _logger;
        private readonly string _ffmpegPath;

        public TTSService(IConfiguration configuration, IWebHostEnvironment env, ILogger<TTSService> logger)
        {
            _env = env;
            _ffmpegPath = FfmpegLocator.Resolve(configuration, env.WebRootPath);
            _logger = logger;
            _client = new Client(
                apiKey: configuration["Gemini:ApiKey"]
            );
        }

        public async Task<string> GenerateSpeechAsync(string text)
        {
            var model = "gemini-2.5-flash-preview-tts";

            var contents = new List<Content>
            {
                new Content
                {
                    Role = "user",
                    Parts = new List<Part>
                    {
                        new Part
                        {
                            Text = $"## Transcript:\n{text}"
                        }
                    }
                }
            };

            var config = new GenerateContentConfig
            {
                Temperature = 1,

                ResponseModalities = new List<string>
                {
                    "audio"
                },

                SpeechConfig = new SpeechConfig
                {
                    VoiceConfig = new VoiceConfig
                    {
                        PrebuiltVoiceConfig = new PrebuiltVoiceConfig
                        {
                            VoiceName = "Leda"
                        }
                    }
                }
            };

            byte[]? audioBytes = null;
            var generationTimer = Stopwatch.StartNew();
            try
            {
                await foreach (var chunk in _client.Models.GenerateContentStreamAsync(
                                   model,
                                   contents,
                                   config))
                {
                    if (chunk.Candidates == null ||
                        chunk.Candidates.Count == 0 ||
                        chunk.Candidates[0].Content?.Parts == null)
                    {
                        continue;
                    }

                    var part = chunk.Candidates[0].Content.Parts[0];

                    if (part.InlineData?.Data != null)
                    {
                        audioBytes = part.InlineData.Data;

                        Console.WriteLine($"MimeType: {part.InlineData.MimeType}");

                        break;
                    }
                }
            }
            finally
            {
                _logger.LogInformation("Audio stage {Stage} completed in {DurationMs} ms", "gemini_tts_generation", generationTimer.ElapsedMilliseconds);
            }

            if (audioBytes == null)
                throw new Exception("Failed to generate TTS audio.");

            // Save PCM as WAV
            var saveTimer = Stopwatch.StartNew();
            string wavPath;
            try
            {
                wavPath = await SavePcmAsWav(audioBytes);
            }
            finally
            {
                _logger.LogInformation("Audio stage {Stage} completed in {DurationMs} ms", "pcm_to_wav", saveTimer.ElapsedMilliseconds);
            }

            // Convert WAV -> OGG OPUS for Telegram voice note
            string oggPath;
            var conversionTimer = Stopwatch.StartNew();
            try
            {
                oggPath = await ConvertToTelegramVoice(wavPath);
            }
            finally
            {
                _logger.LogInformation("Audio stage {Stage} completed in {DurationMs} ms", "ffmpeg_tts_conversion", conversionTimer.ElapsedMilliseconds);
                // WAV hanya perantara; yang dikirim ke Telegram adalah OGG.
                TryDelete(wavPath);
            }

            return oggPath;
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private async Task<string> SavePcmAsWav(byte[] pcmData)
        {
            var folderPath = Path.Combine("wwwroot", "tts");

            if (!Directory.Exists(folderPath))
                Directory.CreateDirectory(folderPath);

            var fileName = $"{Guid.NewGuid()}.wav";

            var fullPath = Path.Combine(folderPath, fileName);

            using var fs = new FileStream(fullPath, FileMode.Create);
            using var writer = new BinaryWriter(fs);

            short channels = 1;
            int sampleRate = 24000;
            short bitsPerSample = 16;

            int byteRate = sampleRate * channels * bitsPerSample / 8;
            short blockAlign = (short)(channels * bitsPerSample / 8);

            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));

            writer.Write(36 + pcmData.Length);

            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);

            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(pcmData.Length);
            writer.Write(pcmData);

            await fs.FlushAsync();

            return fullPath;
        }

        private async Task<string> ConvertToTelegramVoice(string wavPath)
        {
            var oggPath = Path.ChangeExtension(wavPath, ".ogg");
            var ffmpegPath = _ffmpegPath;

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments =
                        $"-y -i \"{wavPath}\" -c:a libopus \"{oggPath}\"",

                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            string error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            Console.WriteLine(error);

            if (!System.IO.File.Exists(oggPath))
                throw new Exception("Failed to convert audio to Telegram voice format.");

            return oggPath;
        }
    }
}