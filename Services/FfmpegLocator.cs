namespace CS_AI.Services
{
    public static class FfmpegLocator
    {
        // Urutan: 1) Ffmpeg:Path di konfigurasi, 2) salinan bawaan di wwwroot (jika masih ada),
        // 3) "ffmpeg" dari PATH server (cukup install ffmpeg dan pastikan `ffmpeg -version` jalan).
        public static string Resolve(IConfiguration configuration, string? webRootPath)
        {
            var configured = configuration["Ffmpeg:Path"];
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            if (!string.IsNullOrEmpty(webRootPath))
            {
                foreach (var relative in new[] { Path.Combine("ffmpeg", "bin", "ffmpeg.exe"), Path.Combine("ffmpeg", "ffmpeg.exe") })
                {
                    var candidate = Path.Combine(webRootPath, relative);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return "ffmpeg";
        }
    }
}
