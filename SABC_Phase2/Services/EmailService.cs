using Microsoft.Data.SqlClient;
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

Thank you for your submission. The SABC has received your application and it is currently under review. For any further queries, please visit the SABC Help Site or email Tendersqueries@sabc.co.za 

Tender Number: {tenderNumber}
Tender Name: {tenderName}
Time submitted: {submittedAt:yyyy-MM-dd HH:mm:ss}

Yours Sincerely,
SABC SCM";

            using var mailMessage = new MailMessage(fromEmail, new MailAddress(toEmail))
            {
                Subject = "SABC Tender Submission Confirmation",
                Body = body,
                IsBodyHtml = false
            };

            await smtpClient.SendMailAsync(mailMessage);
        }

        public async Task SendPasswordResetEmailAsync(string toEmail, string resetLink)
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

            // Try to get company name from tbl_suppliers (legacy DB)
            string companyName = "Supplier";
            try
            {
                var legacyConnStr = _configuration.GetConnectionString("LegacyDb");
                using var conn = new SqlConnection(legacyConnStr);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT TOP 1 ISNULL(tradingname, legalname) FROM tbl_suppliers WHERE email=@Email", conn);
                cmd.Parameters.AddWithValue("@Email", toEmail);
                var result = await cmd.ExecuteScalarAsync();
                if (result != null && !string.IsNullOrWhiteSpace(result.ToString()))
                    companyName = result.ToString();
            }
            catch
            {
                // fallback to default if lookup fails
            }

            var body = $@"Dear {companyName}

We’ve received a request to reset the password for your account. Please use the link below to proceed. For your security, the link will expire in 24 hours:

{resetLink}

If you did not request a password reset, please ignore this message or contact support.

Yours Sincerely,
SABC Support Team";

            using var mailMessage = new MailMessage(fromEmail, new MailAddress(toEmail))
            {
                Subject = "SABC Password Reset",
                Body = body,
                IsBodyHtml = false
            };

            await smtpClient.SendMailAsync(mailMessage);
        }
        public async Task SendTenderClosedNotificationAsync(
    IEnumerable<string> adminEmails, string tenderNumber, string tenderTitle, DateTime closingDateTime)
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

            var body = $@"Dear Administrator,

The following tender has been automatically closed by the system:

Tender Number: {tenderNumber}
Tender Title: {tenderTitle}
Closed At: {closingDateTime:yyyy-MM-dd HH:mm:ss}

If this was unexpected, please review the tender's configuration.

Yours Sincerely,
SABC SCM";

            foreach (var adminEmail in adminEmails.Distinct())
            {
                using var mailMessage = new MailMessage(fromEmail, new MailAddress(adminEmail))
                {
                    Subject = $"SABC Tender Closed Notification: {tenderNumber}",
                    Body = body,
                    IsBodyHtml = false
                };
                await smtpClient.SendMailAsync(mailMessage);
            }
        }

        /// <summary>
        /// Sends account creation notification email to newly created administrator users
        /// </summary>
        public async Task SendNewUserAccountEmailAsync(string toEmail, string firstName, string lastName, string role, string temporaryPassword)
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

            // Format role for display
            string roleDisplay = role switch
            {
                "IT_Admin" => "IT Administrator",
                "Tender_Administrator" => "Tender Administrator",
                "Vendor_Administrator" => "Vendor Administrator",
                _ => role
            };

            var body = $@"Dear {firstName} {lastName},

Your account has been created on the OVRS Platform as a {roleDisplay}. Below are your login credentials:

Email: {toEmail}
Temporary Password: {temporaryPassword}

For security reasoans, please log in as soon as possible and update your password.

Best regards,
IT Admin | SABC";

            using var mailMessage = new MailMessage(fromEmail, new MailAddress(toEmail))
            {
                Subject = "Your New Internal User Account Has Been Created",
                Body = body,
                IsBodyHtml = false
            };

            await smtpClient.SendMailAsync(mailMessage);
        }
    }
}