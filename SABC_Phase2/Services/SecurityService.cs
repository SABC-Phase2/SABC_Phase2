using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SABC_Phase2.Models.Security;
using Microsoft.Graph;

namespace SABC_Phase2.Services
{
    public interface ISecurityService
    {
        string GenerateDataHash(string firstName, string lastName, string email, string azureAdId, string nonce, long timestamp);
        string GenerateNonce();
        long GetCurrentTimestamp();
        FormTamperDetectionResult ValidateFormIntegrity(SecureCreateUserRequest request, string secretKey);
        Task<FormTamperDetectionResult> ValidateAzureUserData(string azureAdId, string firstName, string lastName, string email, GraphServiceClient graphClient);
        bool IsTimestampValid(long timestamp, int maxAgeMinutes = 30);
    }

    public class SecurityService : ISecurityService
    {
        private readonly ILogger<SecurityService> _logger;
        private const int MAX_FORM_AGE_MINUTES = 30;

        public SecurityService(ILogger<SecurityService> logger)
        {
            _logger = logger;
        }

        public string GenerateDataHash(string firstName, string lastName, string email, string azureAdId, string nonce, long timestamp)
        {
            var data = $"{firstName}|{lastName}|{email}|{azureAdId}|{nonce}|{timestamp}";
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash);
        }

        public string GenerateNonce()
        {
            var bytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        public long GetCurrentTimestamp()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        public FormTamperDetectionResult ValidateFormIntegrity(SecureCreateUserRequest request, string secretKey)
        {
            try
            {
                // Validate timestamp
                if (!IsTimestampValid(request.Timestamp))
                {
                    _logger.LogWarning("Form submission with expired timestamp: {Timestamp}", request.Timestamp);
                    return new FormTamperDetectionResult
                    {
                        IsTampered = true,
                        TamperType = "EXPIRED_FORM",
                        Message = "Form has expired. Please refresh and try again.",
                        Details = "Timestamp validation failed"
                    };
                }

                // Generate expected hash
                var expectedHash = GenerateDataHash(
                    request.FirstName,
                    request.LastName,
                    request.Email,
                    request.AzureAdId,
                    request.Nonce,
                    request.Timestamp
                );

                // Compare hashes
                if (expectedHash != request.DataHash)
                {
                    _logger.LogWarning("Form tampering detected - hash mismatch. Expected: {Expected}, Received: {Received}",
                        expectedHash, request.DataHash);

                    return new FormTamperDetectionResult
                    {
                        IsTampered = true,
                        TamperType = "DATA_MODIFIED",
                        Message = "Form data has been tampered with. Please refresh and try again.",
                        Details = "Hash validation failed"
                    };
                }

                return new FormTamperDetectionResult
                {
                    IsTampered = false,
                    Message = "Form integrity validated successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating form integrity");
                return new FormTamperDetectionResult
                {
                    IsTampered = true,
                    TamperType = "VALIDATION_ERROR",
                    Message = "Unable to validate form integrity. Please try again.",
                    Details = ex.Message
                };
            }
        }

        public async Task<FormTamperDetectionResult> ValidateAzureUserData(string azureAdId, string firstName, string lastName, string email, GraphServiceClient graphClient)
        {
            try
            {
                if (string.IsNullOrEmpty(azureAdId))
                {
                    return new FormTamperDetectionResult
                    {
                        IsTampered = true,
                        TamperType = "MISSING_AZURE_ID",
                        Message = "Azure AD user ID is required",
                        Details = "Missing Azure AD ID"
                    };
                }

                // Fetch user from Azure AD
                var azureUser = await graphClient.Users[azureAdId]
                    .GetAsync(requestConfiguration =>
                    {
                        requestConfiguration.QueryParameters.Select = new string[] { "id", "displayName", "givenName", "surname", "mail", "userPrincipalName" };
                    });

                if (azureUser == null)
                {
                    _logger.LogWarning("Azure AD user not found: {AzureAdId}", azureAdId);
                    return new FormTamperDetectionResult
                    {
                        IsTampered = true,
                        TamperType = "USER_NOT_FOUND",
                        Message = "Selected user not found in Azure AD",
                        Details = "Azure AD user validation failed"
                    };
                }

                // Validate the data matches
                var azureFirstName = azureUser.GivenName ?? "";
                var azureLastName = azureUser.Surname ?? "";
                var azureEmail = azureUser.Mail ?? azureUser.UserPrincipalName ?? "";

                if (!string.Equals(firstName, azureFirstName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(lastName, azureLastName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(email, azureEmail, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Azure AD data mismatch. Submitted: {SubmittedFirst} {SubmittedLast} {SubmittedEmail}, Azure: {AzureFirst} {AzureLast} {AzureEmail}",
                        firstName, lastName, email, azureFirstName, azureLastName, azureEmail);

                    return new FormTamperDetectionResult
                    {
                        IsTampered = true,
                        TamperType = "AZURE_DATA_MISMATCH",
                        Message = "Submitted user data does not match Azure AD records",
                        Details = "Azure AD validation failed - data mismatch"
                    };
                }

                return new FormTamperDetectionResult
                {
                    IsTampered = false,
                    Message = "Azure AD validation successful"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating Azure AD user data for ID: {AzureAdId}", azureAdId);
                return new FormTamperDetectionResult
                {
                    IsTampered = true,
                    TamperType = "AZURE_VALIDATION_ERROR",
                    Message = "Unable to validate user with Azure AD. Please try again.",
                    Details = ex.Message
                };
            }
        }

        public bool IsTimestampValid(long timestamp, int maxAgeMinutes = MAX_FORM_AGE_MINUTES)
        {
            var submittedTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            var currentTime = DateTimeOffset.UtcNow;
            var age = currentTime - submittedTime;

            return age.TotalMinutes <= maxAgeMinutes && age.TotalMinutes >= -5; // Allow 5 minutes clock skew
        }




     
    }
}