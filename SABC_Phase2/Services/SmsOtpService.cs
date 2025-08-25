using System.Text;
using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace SABC_Phase2.Services
{
    public class SmsOtpService
    {
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        public SmsOtpService(IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        public async Task SendEmailOtpSmsAsync(string phoneNumber, string userName, string newEmailAddress, string otpCode)
        {
            try
            {
                // Clean phone number format for SMS (remove spaces, ensure proper format)
                string cleanPhoneNumber = CleanPhoneNumber(phoneNumber);

                string message = $"Hello {userName}, you requested to change your email to {newEmailAddress}. Your verification code is: {otpCode}. This code expires in 5 minutes. - SABC SCM";

                await SendSmsAsync(cleanPhoneNumber, message);
            }
            catch (Exception ex)
            {
                // Log the error
                Console.WriteLine($"Error sending SMS OTP: {ex.Message}");
                throw;
            }
        }

        public async Task SendPhoneOtpSmsAsync(string phoneNumber, string userName, string otpCode)
        {
            try
            {
                // Clean phone number format for SMS
                string cleanPhoneNumber = CleanPhoneNumber(phoneNumber);

                string message = $"Hello {userName}, your phone verification code is: {otpCode}. This code expires in 5 minutes. - SABC SCM";

                await SendSmsAsync(cleanPhoneNumber, message);
            }
            catch (Exception ex)
            {
                // Log the error
                Console.WriteLine($"Error sending SMS OTP: {ex.Message}");
                throw;
            }
        }

        private async Task SendSmsAsync(string phoneNumber, string message)
        {
            var smsSettings = _configuration.GetSection("SmsSettings");
            string accountSid = smsSettings["AccountSid"];
            string authToken = smsSettings["AuthToken"];
            string fromPhone = smsSettings["FromPhoneNumber"];

            if (string.IsNullOrEmpty(accountSid) || string.IsNullOrEmpty(authToken))
            {
                // For development - just log the SMS
                Console.WriteLine($"SMS to {phoneNumber}: {message}");
                Console.WriteLine("Note: Twilio settings not configured. Message logged instead of sent.");
                return;
            }

            using var httpClient = _httpClientFactory.CreateClient();

            // Twilio API authentication
            string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}"));
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Basic {credentials}");

            var formParams = new List<KeyValuePair<string, string>>
    {
        new("To", phoneNumber),
        new("From", fromPhone),
        new("Body", message)
    };

            var formContent = new FormUrlEncodedContent(formParams);
            var apiUrl = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/Messages.json";

            var response = await httpClient.PostAsync(apiUrl, formContent);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Twilio SMS Error: {response.StatusCode} - {errorContent}");
            }

            Console.WriteLine($"SMS sent successfully via Twilio to {phoneNumber}");
        }

        private string CleanPhoneNumber(string phoneNumber)
        {
            if (string.IsNullOrEmpty(phoneNumber))
                return phoneNumber;

            // Remove all spaces and special characters except +
            string cleaned = phoneNumber.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");

            // Ensure it starts with + for international format
            if (!cleaned.StartsWith("+"))
            {
                cleaned = "+27" + cleaned; // Default to South African code if none provided
            }

            return cleaned;
        }
    }
}