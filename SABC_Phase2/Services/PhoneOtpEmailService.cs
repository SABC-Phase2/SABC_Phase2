using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace SABC_Phase2.Services
{
    public class PhoneOtpEmailService
    {
        private readonly IConfiguration _configuration;

        public PhoneOtpEmailService(IConfiguration configuration)
        {
            _configuration = configuration;
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

        public async Task SendEmailOtpAsync(string newEmail, string userName, string otpCode)
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
        }
    }
}