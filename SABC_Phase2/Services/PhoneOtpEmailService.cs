using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SABC_Phase2.Services
{
    public class EmailDeliveryResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public bool IsInvalidEmail { get; set; }
    }
    public class PhoneOtpEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<PhoneOtpEmailService> _logger;

        public PhoneOtpEmailService(IConfiguration configuration, ILogger<PhoneOtpEmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task SendPhoneOtpAsync(string userEmail, string userName, string newPhoneNumber, string otpCode)
        {
            var emailSettings = _configuration.GetSection("EmailSettings");

            using var smtpClient = new SmtpClient(emailSettings["SmtpServer"])
            {
                Port = int.Parse(emailSettings["SmtpPort"]),
                Credentials = new NetworkCredential(
                    emailSettings["ServiceAccountEmail"],
                    emailSettings["ServiceAccountPassword"]),
                EnableSsl = bool.Parse(emailSettings["EnableSsl"] ?? "true"),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 10000
            };

            var fromEmail = new MailAddress(
                emailSettings["ServiceAccountEmail"],
                $"{emailSettings["FromName"]}");

            var body = $@"Dear {userName},

You have requested to change your phone number to {newPhoneNumber}.

Your verification code is: {otpCode}

This code will expire in 5 minutes for security reasons.

If you did not request this change, please ignore this email and contact support immediately.

For any queries, please email Tendersqueries@SABC.co.za

Yours Sincerely,
SABC SCM Team";

            using var mailMessage = new MailMessage(fromEmail, new MailAddress(userEmail))
            {
                Subject = "Phone Number Change - Verification Code",
                Body = body,
                IsBodyHtml = false
            };

            await smtpClient.SendMailAsync(mailMessage);
        }

        public async Task<EmailDeliveryResult> SendEmailOtpAsync(string newEmail, string userName, string otpCode)
        {
            var result = new EmailDeliveryResult();

            try
            {
                var emailSettings = _configuration.GetSection("EmailSettings");

                using var smtpClient = new SmtpClient(emailSettings["SmtpServer"])
                {
                    Port = int.Parse(emailSettings["SmtpPort"]),
                    Credentials = new NetworkCredential(
                        emailSettings["ServiceAccountEmail"],
                        emailSettings["ServiceAccountPassword"]),
                    EnableSsl = bool.Parse(emailSettings["EnableSsl"] ?? "true"),
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Timeout = 15000 // Increased timeout
                };

                var fromEmail = new MailAddress(
                    emailSettings["ServiceAccountEmail"],
                    $"{emailSettings["FromName"]}");

                var body = $@"Dear {userName},

You have requested to change your email address to this email address.

Your verification code is: {otpCode}

This code will expire in 5 minutes for security reasons.

If you did not request this change, please ignore this email.

For any queries, please email Tendersqueries@SABC.co.za

Yours Sincerely,
SABC SCM Team";

                using var mailMessage = new MailMessage(fromEmail, new MailAddress(newEmail))
                {
                    Subject = "Email Address Change - Verification Code",
                    Body = body,
                    IsBodyHtml = false
                };

                await smtpClient.SendMailAsync(mailMessage);
                result.Success = true;
            }
            catch (SmtpException ex)
            {
                result.Success = false;
                result.ErrorMessage = "Failed to send email. Please check the email address and try again.";
                result.IsInvalidEmail = IsEmailRelatedError(ex);

                _logger.LogWarning($"SMTP Error sending to {newEmail}: {ex.Message}");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = "An error occurred while sending the verification email.";
                result.IsInvalidEmail = false;

                _logger.LogError(ex, $"Error sending email to {newEmail}");
            }

            return result;
        }

        private bool IsEmailRelatedError(SmtpException ex)
        {
            var message = ex.Message.ToLower();
            return message.Contains("mailbox") ||
                   message.Contains("recipient") ||
                   message.Contains("address") ||
                   message.Contains("550") ||
                   message.Contains("551") ||
                   message.Contains("553");
        }

    }
}