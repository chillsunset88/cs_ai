namespace CS_AI.Services
{
    public static class PhoneNumberHelper
    {
        // Menyeragamkan format nomor agar bisa dibandingkan dan dipakai sebagai tujuan WhatsApp:
        // "0812-3456-7890", "+62 812 3456 7890", "6281234567890" -> "6281234567890".
        // Mengembalikan null jika bukan nomor yang masuk akal (8-15 digit).
        public static string? Normalize(string? raw, string defaultCountryCode = "62")
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Length == 0)
                return null;

            if (digits.StartsWith("00"))
            {
                digits = digits[2..];
            }
            else if (digits.StartsWith('0'))
            {
                digits = defaultCountryCode + digits[1..];
            }

            return digits.Length is >= 8 and <= 15 ? digits : null;
        }

        // Untuk log: jangan menulis nomor lengkap.
        public static string Mask(string phone) =>
            phone.Length <= 6 ? new string('*', phone.Length) : phone[..4] + new string('*', phone.Length - 7) + phone[^3..];
    }
}
