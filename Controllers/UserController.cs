using CS_AI.Models;
using CS_AI.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.Design;
using System.DirectoryServices.AccountManagement;


namespace CS_AI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UserController : Controller
    {
        private readonly ILogger<UserController> _logger;
        // Dibaca dari konfigurasi (Ldap:*), bukan hardcoded.
        private readonly string AD_SERVER;
        private readonly string AD_USERNAME;
        private readonly string AD_PASSWORD;

        private readonly LDAPService _ldapService;
        private readonly STTService _serviceSTT;
        public UserController(LDAPService ldapService, ILogger<UserController> logger, STTService serviceSTT, IConfiguration configuration)
        {
            _ldapService = ldapService;
            _logger = logger;
            _serviceSTT = serviceSTT;
            AD_SERVER = configuration["Ldap:Server"] ?? "";
            AD_USERNAME = configuration["Ldap:Username"] ?? "";
            AD_PASSWORD = configuration["Ldap:Password"] ?? "";
        }

        //private const string SEARCH_USERNAME = "prayogga.rio";

        [HttpGet("getUser")]
        public IActionResult GetUser(string username)
        {
            try
            {
                var user = _ldapService.GetUser(username);

                if (user == null)
                {
                    return NotFound(new
                    {
                        Found = false,
                        Message = $"User '{username}' tidak ditemukan"
                    });
                }

                return Ok(new
                {
                    Found = true,
                    Username = user.SamAccountName ?? "",
                    DisplayName = user.DisplayName ?? "",
                    DistinguishedName = user.DistinguishedName ?? ""
                });
            }
            catch (Exception ex)
            {
                //Console.Write("error");
                _logger.LogWarning(ex.Message);
                return StatusCode(500, new
                {
                    Found = false,
                    Error = ex.Message
                });
            }
        }

        [HttpPost("resetPassword")]
        public IActionResult ResetPassword(UserModel data)
        {
            if (data.New_Password == null || data.Confirm_Password == null)
            {
                return StatusCode(400, new { message = "Invalid Parameter" });
            }
            if (data.New_Password != data.Confirm_Password)
            {
                return StatusCode(400, new { message = "The Password doesn't match" });
            }
            try
            {
                if (AD_SERVER == null)
                {
                    return StatusCode(400, new { message = "Server doesn't exist" });
                }
                using (var context = new PrincipalContext(ContextType.Domain, AD_SERVER, AD_USERNAME, AD_PASSWORD))
                {
                    using (var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, data.Username))
                    {
                        if (user != null)
                        {
                            user.SetPassword(data.New_Password);
                            user.Save();

                            return StatusCode(200, new { message = "Success" });
                        }
                        else
                        {
                            return StatusCode(400, new { message = "User not found" });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.InnerException });
            }
        }

        [HttpPost("changePassword")]
        public IActionResult ChangePassword(UserModel data)
        {
            if (data.Current_Password == null || data.New_Password == null || data.Confirm_Password == null)
            {
                return StatusCode(400, new { message = "Invalid Parameter" });
            }
            if (data.New_Password != data.Confirm_Password)
            {
                return StatusCode(400, new { mesaage = "The Password doesn't match" });
            }
            try
            {
                if (AD_SERVER == null)
                {
                    return StatusCode(400, new { message = "Server doesn't exist" });
                }
                using var contextUser = new PrincipalContext(ContextType.Domain, AD_SERVER, data.Username, data.Current_Password);
                if (contextUser != null)
                {
                    using (var context = new PrincipalContext(ContextType.Domain, AD_SERVER, AD_USERNAME, AD_PASSWORD))
                    {
                        using (var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, data.Username))
                        {
                            if (user != null)
                            {
                                user.SetPassword(data.New_Password);
                                user.Save();

                                return StatusCode(200, new { message = "Success" });
                            }
                            else
                            {
                                return StatusCode(400, new { message = "User not found" });
                            }
                        }
                    }
                }
                else
                {
                    return StatusCode(400, new { message = "Current password invalid." });
                }
            }
            catch (Exception ex)
            {
                return StatusCode(400, new { message = ex.InnerException });
            }
        }
        [HttpPost("STTTest")]
        public async Task<IActionResult> STTTest(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { message = "No file uploaded" });
            }

            var tempFilePath = Path.GetTempFileName();

            try
            {
                using (var stream = new FileStream(tempFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var result = await _serviceSTT.SpeechToText(tempFilePath);

                return Ok(new { text = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
            finally
            {
                if (System.IO.File.Exists(tempFilePath))
                {
                    System.IO.File.Delete(tempFilePath);
                }
            }
        }
    }
}
