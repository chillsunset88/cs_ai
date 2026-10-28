using CS_AI.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace CS_AI.Controllers
{
    public class AccountController : Controller
    {
        private readonly LDAPService _ldapService;
        private readonly HttpClient _httpClient;
        private readonly AICoreService _aiService;
        private readonly PasswordResetTokenService _resetTokens;

        public AccountController(LDAPService ldapService, HttpClient httpClient, AICoreService lmStudioService, PasswordResetTokenService resetTokens)
        {
            _ldapService = ldapService;
            _httpClient = httpClient;
            _aiService = lmStudioService;
            _resetTokens = resetTokens;
        }
        public IActionResult Index()
        {
            return View();
        }

        [HttpGet]
        public IActionResult ResetPassword(string? token)
        {
            NoStore();

            // Halaman hanya bisa dibuka lewat link dari bot; tanpa token yang valid tidak ada form sama sekali.
            if (string.IsNullOrEmpty(token) || !_resetTokens.TryGetUsername(token, out var tokenUser))
            {
                ViewBag.TokenInvalid = true;
                return View();
            }

            ViewBag.Token = token;
            ViewBag.Username = tokenUser;
            ViewBag.Step = _resetTokens.IsVerified(token) ? "password" : "otp";
            return View();
        }

        [HttpGet]
        public IActionResult ResetSuccess()
        {
            return View();
        }

        //[HttpPost]
        //public async Task<IActionResult> ResetPassword(string Username, string Password, string ConfirmPassword)
        //{

        //    if (Password != ConfirmPassword)
        //    {
        //        ViewBag.Message = "Passwords do not match.";
        //        ViewBag.Success = false;
        //        return View();
        //    }
        //    //bool Success = true;
        //    bool Success = _ldapService.ResetPassword(Username, Password);
        //    if (Success==true)
        //    {
        //        ViewBag.Message = "Password has been reset successfully!";
        //        _ = Task.Run(async () =>
        //        {
        //            var payload = new GenerateResponseRequest
        //            {
        //                Prompt = $"message = Password berhail direset",
        //                SessionId = "Caesar",
        //                Model = "qwen2.5vl:7b",
        //                ImagePath = null
        //            };
        //            var result = await _aiService.GenerateResponse(payload);
        //            var regexSystem = Regex.Match(result, @"\""message\""\s*:\s*\""([^\""]+)\""");
        //            if (regexSystem.Success)
        //            {
        //                _aiService.SendMessage(regexSystem.Groups[1].Value);
        //            }
        //        });
        //    }
        //    else
        //    {
        //        ViewBag.Message = "Passwords doesn't reset.";
        //    }
        //    return View();
        //}
        [HttpPost]
        public IActionResult VerifyOtp(string? Token, string? Otp)
        {
            NoStore();

            if (string.IsNullOrEmpty(Token) || !_resetTokens.TryGetUsername(Token, out var username))
            {
                ViewBag.TokenInvalid = true;
                return View("ResetPassword");
            }

            ViewBag.Token = Token;
            ViewBag.Username = username;

            switch (_resetTokens.VerifyOtp(Token, Otp))
            {
                case OtpResult.Ok:
                    ViewBag.Step = "password";
                    return View("ResetPassword");

                case OtpResult.Invalid:
                    ViewBag.Message = "Kode OTP salah. Periksa kode di WhatsApp Anda lalu coba lagi.";
                    break;

                case OtpResult.Locked:
                    // Terlalu banyak percobaan: sesi dibuang, user harus meminta reset ulang lewat bot.
                    _resetTokens.Consume(Token);
                    ViewBag.TokenInvalid = true;
                    return View("ResetPassword");

                default:
                    _resetTokens.Consume(Token);
                    ViewBag.TokenInvalid = true;
                    return View("ResetPassword");
            }

            ViewBag.Step = "otp";
            ViewBag.Success = false;
            return View("ResetPassword");
        }

        [HttpPost]
        public async Task<IActionResult> ResetPassword(string? Password, string? ConfirmPassword, string? Token)
        {
            NoStore();
            return await ResetPasswordWithTokenAsync(Token ?? string.Empty, Password, ConfirmPassword);
        }

        // Token berisi nama user, jadi halaman ini tidak pernah memakai username dari form.
        private void NoStore()
        {
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers["Referrer-Policy"] = "no-referrer";
        }

        private async Task<IActionResult> ResetPasswordWithTokenAsync(string token, string? password, string? confirmPassword)
        {
            // Username SELALU diambil dari token, bukan dari form, supaya link hanya bisa dipakai untuk akun yang lolos OTP.
            if (!_resetTokens.TryGetUsername(token, out var username))
            {
                ViewBag.TokenInvalid = true;
                return View();
            }

            ViewBag.Token = token;
            ViewBag.Username = username;

            if (!_resetTokens.IsVerified(token))
            {
                ViewBag.Step = "otp";
                ViewBag.Message = "Masukkan kode OTP terlebih dahulu.";
                ViewBag.Success = false;
                return View();
            }

            ViewBag.Step = "password";

            if (string.IsNullOrEmpty(password))
            {
                ViewBag.Message = "Semua kolom wajib diisi.";
                ViewBag.Success = false;
                return View();
            }

            if (password != confirmPassword)
            {
                ViewBag.Message = "Konfirmasi password tidak sama.";
                ViewBag.Success = false;
                return View();
            }

            if (password.Length < 8)
            {
                ViewBag.Message = "Password minimal 8 karakter.";
                ViewBag.Success = false;
                return View();
            }

            try
            {
                if (_ldapService.SetNewPassword(username, password))
                {
                    _resetTokens.Consume(token);
                    try
                    {
                        await TriggerAINotification(true);
                    }
                    catch (Exception)
                    {
                        // Notifikasi hanya pelengkap; password sudah berhasil diganti.
                    }

                    return RedirectToAction("ResetSuccess");
                }

                ViewBag.Message = "Password gagal diubah. Pastikan password memenuhi kebijakan perusahaan (panjang dan kompleksitas), lalu coba lagi.";
            }
            catch (Exception)
            {
                ViewBag.Message = "Terjadi kendala saat memproses permintaan. Silakan coba lagi.";
            }

            ViewBag.Success = false;
            return View();
        }

        private async Task TriggerAINotification(bool success)
        {
            var payload = new GenerateResponseRequest
            {
                Prompt = $"The user password reset status is: {success}. Generate a short confirmation message (indonesia).",
                SessionId = "Caesar"
            };

            var result = await _aiService.GenerateResponse(payload);
            var match = Regex.Match(result, @"\""message\""\s*:\s*\""([^\""]+)\""");
            if (match.Success)
            {
                await _aiService.SendMessage(match.Groups[1].Value);
            }
        }
    }
}
