//using Microsoft.AspNetCore.Mvc;

//[ApiController]
//[Route("api/[controller]")]
//public class ActiveDirectoryController : ControllerBase
//{
//    private readonly ActiveDirectoryService _adService;

//    public ActiveDirectoryController(ActiveDirectoryService adService)
//    {
//        _adService = adService;
//    }

//    [HttpPost("reset-password")]
//    public IActionResult ResetPassword([FromBody] ResetPasswordRequest request)
//    {
//        var result = _adService.ResetPassword(request.Username, request.NewPassword);

//        if (!result.Success)
//            return BadRequest(result.Message);

//        return Ok(result.Message);
//    }

//    [HttpPost("check-user")]
//    public IActionResult CheckUser([FromBody] string user)
//    {
//        try
//        {
//            var result = _adService.CheckUserExists(user);
//            if (!result)
//                return NotFound("User not found");
//            return Ok("User exists");
//        }
//        catch (Exception e)
//        {
//            return Ok(e.Message);
//        }
        
//    }
//}

//public class ResetPasswordRequest
//{
//    public string Username { get; set; }
//    public string NewPassword { get; set; }
//}