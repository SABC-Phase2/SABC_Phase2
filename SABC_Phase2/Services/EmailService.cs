using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace SABC_Phase2.Services
{
    public class EmailService
    {
        private readonly IConfiguration _configuration;

        public EmailService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task SendTenderSubmissionConfirmationAsync(string toEmail, string userName, string tenderNumber, string tenderName, DateTime submittedAt)
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

Thank you for submitting your application, SABC has received your application and it is now under review.
For any further queries, please visit the SABC Help site or email Tendersqueries@SABC.co.za

Tender Number: {tenderNumber}
Tender Name: {tenderName}
Time submitted: {submittedAt:yyyy-MM-dd HH:mm:ss}

Yours Sincerely,
SABC SCM";

            using var mailMessage = new MailMessage(fromEmail, new MailAddress(toEmail))
            {
                Subject = "SABC Tender Application Confirmation",
                Body = body,
                IsBodyHtml = false
            };

            await smtpClient.SendMailAsync(mailMessage);
        }
    }
}