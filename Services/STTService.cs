using Whisper.net;
using Microsoft.AspNetCore.Hosting;
using System.Text;

namespace CS_AI.Services
{
    public class STTService : IDisposable
    {
        private readonly IWebHostEnvironment _env;
        private readonly Lazy<WhisperFactory> _factory;
        // Whisper berjalan di CPU; satu transkripsi pada satu waktu supaya pesan suara bersamaan tidak saling menghambat.
        private readonly SemaphoreSlim _gate = new(1, 1);

        public STTService(IWebHostEnvironment env)
        {
            _env = env;
            // Model ggml-small.bin (~466 MB) dimuat sekali saja, bukan per pesan suara.
            _factory = new Lazy<WhisperFactory>(
                () => WhisperFactory.FromPath(Path.Combine(_env.WebRootPath, "ggml-small.bin")),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public async Task<string> SpeechToText(string filePath, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                // Factory boleh dibagi; processor tidak thread-safe sehingga dibuat per panggilan.
                await using var processor = _factory.Value.CreateBuilder()
                    .WithLanguage("id")
                    // Petunjuk kosakata agar istilah layanan tidak salah dengar (mis. "password" jadi "pasword").
                    .WithPrompt("Ganti password, reset password, lupa password, username, kode OTP, billing, nomor billing, tagihan, laporan jalan rusak, alamat.")
                    .Build();

                await using var fileStream = File.OpenRead(filePath);

                var result = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(fileStream, cancellationToken))
                {
                    result.Append(segment.Text);
                }

                return result.ToString();
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            if (_factory.IsValueCreated)
            {
                _factory.Value.Dispose();
            }
            _gate.Dispose();
        }
    }
}
