namespace CS_AI.Models
{
    public class UserModel
    {
        public string Username { get; set; }
        public string? Current_Password { get; set; }
        public string? New_Password { get; set; }
        public string? Confirm_Password { get; set; }
    }
}