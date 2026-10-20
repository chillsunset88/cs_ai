using CS_AI.Services;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5000);
    //options.ListenLocalhost(5001, listenOptions =>
    //{
    //    listenOptions.UseHttps();
    //});
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
// Satu handler pool untuk semua HttpClient (LLM, WhatsApp, dst) supaya koneksi TCP/TLS dipakai ulang.
builder.Services.ConfigureHttpClientDefaults(http =>
{
    http.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        MaxConnectionsPerServer = 32,
        EnableMultipleHttp2Connections = true,
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    });
    http.SetHandlerLifetime(TimeSpan.FromMinutes(5));
});

// TTS dan STT harus singleton (STT meng-cache model Whisper di memori).
builder.Services.AddSingleton<TTSService>();
builder.Services.AddSingleton<STTService>();
builder.Services.AddScoped<LDAPService>();
builder.Services.AddSingleton<PasswordResetTokenService>();
//builder.Services.AddScoped<ActiveDirectoryService>();
//builder.Services.Configure<ActiveDirectorySettings>(
//    builder.Configuration.GetSection("ActiveDirectory"));

//builder.Services.AddHostedService<MQTTService>();
builder.Services.AddSingleton<MQTTService>();
builder.Services.AddSingleton<TelegramBotService>();
// Resolve dari singleton yang sama; AddHostedService<T>() saja membuat instance kedua.
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramBotService>());
//builder.Services.AddHostedService(provider => provider.GetRequiredService<MQTTService>());

//builder.Services.AddHttpClient<AICoreService>(client =>
//{
//    client.BaseAddress = new Uri("http://localhost:5000/");
//});

builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("LLM"));
builder.Services.AddHttpClient(AICoreService.LlmClientName, (sp, client) =>
{
    var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
    client.BaseAddress = new Uri(llm.BaseUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", llm.ApiKey);
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<AICoreService>();
//builder.Services.Add<AICoreService>(client =>
//{
//    client. 
//    //var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
//    //return new AICoreService(httpClient, "http://54.169.109.46:5000/");
//});
//builder.Services.AddHttpClient<LmStudioService>(client =>
//{
//    client.BaseAddress = new Uri("http://localhost:1234/");
//});

var app = builder.Build();

//app.UseHttpsRedirection();

if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");


app.UseAuthorization();
app.MapControllers();

app.Run();
