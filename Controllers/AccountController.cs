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
            // Tanpa token: halaman lama (username diisi manual). Dengan token: link dari bot, username terkunci.
            if (!string.IsNullOrEmpty(token))
            {
                if (!_resetTokens.TryGetUsername(token, out var tokenUser))
                {
                    ViewBag.TokenInvalid = true;
                    return View();
                }

                ViewBag.Token = token;
                ViewBag.Username = tokenUser;
            }

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
        public async Task<IActionResult> ResetPassword(string? Username, string? Password, string? ConfirmPassword, string? Token)
        {
            if (!string.IsNullOrEmpty(Token))
            {
                return await ResetPasswordWithTokenAsync(Token, Password, ConfirmPassword);
            }

            if (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password))
            {
                ViewBag.Message = "All fields are required.";
                ViewBag.Success = false;
                return View();
            }

            if (Password != ConfirmPassword)
            {
                ViewBag.Message = "Passwords do not match.";
                ViewBag.Success = false;
                return View();
            }

            try
            {
                bool success = _ldapService.ResetPassword(Username, Password);

                if (success)
                {
                    ViewBag.Message = "Password has been reset successfully!";
                    ViewBag.Success = true;

                    await TriggerAINotification(success);
                    return RedirectToAction("ResetSuccess");
                }
                else
                {
                    ViewBag.Message = "Failed to reset password. Check username or permissions.";
                    ViewBag.Success = false;
                }
            }
            catch (Exception ex)
            {
                ViewBag.Message = "An error occurred during the reset process.";
                ViewBag.Success = false;
            }

            return View();
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

            if (string.IsNullOrEmpty(password))
            {
                ViewBag.Message = "All fields are required.";
                ViewBag.Success = false;
                return View();
            }

            if (password != confirmPassword)
            {
                ViewBag.Message = "Passwords do not match.";
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

                ViewBag.Message = "Failed to reset password. Check username or permissions.";
            }
            catch (Exception)
            {
                ViewBag.Message = "An error occurred during the reset process.";
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
