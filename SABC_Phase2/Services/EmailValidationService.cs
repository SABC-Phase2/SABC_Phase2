using DnsClient;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
// Remove this line: using Microsoft.Graph.Models.Security;

namespace SABC_Phase2.Services
{
    public class EmailValidationService
    {
        private readonly ILogger<EmailValidationService> _logger;

        public EmailValidationService(ILogger<EmailValidationService> logger)
        {
            _logger = logger;
        }

        public bool IsValidEmailFormat(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            try
            {
                // Enhanced format validation
                var emailRegex = new Regex(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$");
                if (!emailRegex.IsMatch(email))
                    return false;

                var addr = new MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        public async Task<EmailValidationResult> ValidateEmailAsync(string email)
        {
            var result = new EmailValidationResult();

            if (!IsValidEmailFormat(email))
            {
                result.IsValid = false;
                result.ErrorMessage = "Please enter a valid email address format.";
                return result;
            }

            if (IsCommonInvalidEmail(email))
            {
                result.IsValid = false;
                result.ErrorMessage = "Please enter a valid email address.";
                return result;
            }

            if (IsDisposableEmail(email))
            {
                result.IsValid = false;
                result.ErrorMessage = "Disposable email addresses are not allowed.";
                return result;
            }

            // Check if domain has MX records
            var domainValidation = await ValidateDomainAsync(email);
            if (!domainValidation.IsValid)
            {
                result.IsValid = false;
                result.ErrorMessage = domainValidation.ErrorMessage;
                return result;
            }

            // For major providers, do additional validation
            if (IsMajorEmailProvider(email))
            {
                var providerValidation = await ValidateWithMajorProvider(email);
                if (!providerValidation.IsValid)
                {
                    result.IsValid = false;
                    result.ErrorMessage = providerValidation.ErrorMessage;
                    return result;
                }
            }

            result.IsValid = true;
            return result;
        }

        private async Task<EmailValidationResult> ValidateDomainAsync(string email)
        {
            var result = new EmailValidationResult();

            try
            {
                var domain = email.Split('@')[1].ToLower();

                // Use DNS lookup for MX records - explicitly specify DnsClient.QueryType
                var lookup = new LookupClient();
                var mxRecords = await lookup.QueryAsync(domain, DnsClient.QueryType.MX);

                if (!mxRecords.Answers.Any())
                {
                    result.IsValid = false;
                    result.ErrorMessage = "Email domain does not exist or cannot receive emails.";
                    return result;
                }

                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Domain validation failed for {email}: {ex.Message}");
                result.IsValid = false;
                result.ErrorMessage = "Unable to verify email domain.";
                return result;
            }
        }

        private bool IsMajorEmailProvider(string email)
        {
            var domain = email.Split('@')[1].ToLower();
            var majorProviders = new[]
            {
                "gmail.com", "yahoo.com", "hotmail.com", "outlook.com",
                "live.com", "icloud.com", "aol.com", "mail.com"
            };

            return majorProviders.Contains(domain);
        }

        private async Task<EmailValidationResult> ValidateWithMajorProvider(string email)
        {
            var result = new EmailValidationResult();
            var domain = email.Split('@')[1].ToLower();

            // For Gmail, check common patterns that are likely invalid
            if (domain == "gmail.com")
            {
                var localPart = email.Split('@')[0].ToLower();

                // Check for suspicious patterns
                if (IsLikelyInvalidGmailPattern(localPart))
                {
                    result.IsValid = false;
                    result.ErrorMessage = "This email address appears to be invalid.";
                    return result;
                }
            }

            // You could add similar checks for other providers

            result.IsValid = true;
            return result;
        }

        private bool IsLikelyInvalidGmailPattern(string localPart)
        {
            // Patterns that are likely to be invalid Gmail addresses
            var suspiciousPatterns = new[]
            {
                //@"^[a-z]{4,}[a-z0-9]*[0-9]{2,}$",       // letters + numbers (like blahuewd3)
                //@"^[a-z]+[0-9]{4,}$",                    // letters followed by many numbers
                @"^test[a-z0-9]*$",                      // starts with "test"
                //@"^[a-z]{3,6}[0-9]{3,6}$",              // short letters + numbers pattern
                @"^(blah|dummy|fake|invalid|temp|spam)[a-z0-9]*$"  // obvious fake patterns
            };

            return suspiciousPatterns.Any(pattern => Regex.IsMatch(localPart, pattern));
        }

        public bool IsCommonInvalidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return true;

            var lowerEmail = email.ToLower();

            // Enhanced patterns for common typos and invalid domains
            var invalidPatterns = new[]
            {
                @"gamil\.com$",                          // gmail typo
                @"gmai\.com$",                           // gmail typo
                @"gmial\.com$",                          // gmail typo
                @"yahooo\.com$",                         // yahoo typo
                @"yaho\.com$",                           // yahoo typo
                @"hotmial\.com$",                        // hotmail typo
                @"hotmali\.com$",                        // hotmail typo
                @"test@test\.com$",                      // test email
                @"example@example\.com$",                // example email
                @"\.con$",                               // .com typo
                @"\.co$",                                // incomplete .com
                @"@\.com$",                              // missing domain name
                @"@com$",                                // missing dot
                @"\.comm$",                              // .com typo
                @"\.cmo$",                               // .com typo
               
                @"^(test|fake|dummy|invalid|temp|spam)[a-z0-9]*@.*$",  // obvious fake patterns
            };

            return invalidPatterns.Any(pattern => Regex.IsMatch(lowerEmail, pattern));
        }

        private bool IsDisposableEmail(string email)
        {
            var domain = email.Split('@')[1].ToLower();

            // List of known disposable email domains
            var disposableDomains = new[]
            {
                "10minutemail.com", "guerrillamail.com", "mailinator.com",
                "tempmail.org", "throwaway.email", "temp-mail.org",
                "getairmail.com", "fakeinbox.com", "yopmail.com",
                "maildrop.cc", "sharklasers.com", "grr.la"
                // Add more as needed
            };

            return disposableDomains.Contains(domain);
        }
    }

    public class EmailValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }
}