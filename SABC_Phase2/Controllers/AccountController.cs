using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SABC_Phase2.Models.Administrator;
using System.Data;
using System.Security.Claims;
using SABC_Phase2.Services; // Ensure this is present for password hashing

namespace SABC_Phase2.Controllers
{
    /// <summary>
    /// Controller responsible for user authentication, including login, logout, and claims-based identity setup.
    /// Handles both administrative and OVRS user (legacy supplier) authentication, with modernized password security.
    /// </summary>
    public class AccountController : Controller
    {
        // Dependency-injected configuration, used to retrieve connection strings and other config values
        private readonly IConfiguration _config;

        public AccountController(IConfiguration config)
        {
            _config = config;
        }

        /// <summary>
        /// GET: /Account/Login
        /// Renders the login page for all users.
        /// </summary>
        [HttpGet]
        public IActionResult Login()
        {
            // Always use explicit path to avoid view resolution ambiguity in enterprise setups.
            return View("~/Views/Authentication/Login.cshtml");
        }

        /// <summary>
        /// POST: /Account/Login
        /// Handles authentication for both administrator and OVRS users (legacy suppliers).
        /// Uses SHA256 hex hashing for secure password validation, matching Phase 1 legacy format.
        /// </summary>
        /// <param name="email">User email address (used for lookup in both admin and supplier tables)</param>
        /// <param name="password">User's plaintext password (will be hashed for comparison)</param>
        /// <returns>Redirects to the appropriate dashboard upon success, or redisplays login on failure.</returns>
        [HttpPost]
        public async Task<IActionResult> Login(string email, string password)
        {
            var legacyConnString = _config.GetConnectionString("LegacyDb");
            var defaultConnString = _config.GetConnectionString("DefaultConn");

            // 1. Attempt ADMINISTRATOR login first
            int? adminId = null;
            int? adminAccountStatus = null;
            string adminFirstName = null, adminLastName = null, adminRole = null, adminPasswordHash = null;

            using (var conn = new SqlConnection(defaultConnString))
            {
                await conn.OpenAsync();
                using (var cmd = new SqlCommand(
                    "SELECT Id, Email, PasswordHash, FirstName, LastName, Role, AccountStatus FROM dbo.Administrators WHERE Email = @Email", conn))
                {
                    cmd.Parameters.AddWithValue("@Email", email);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            adminId = Convert.ToInt32(reader["Id"]);
                            adminPasswordHash = reader["PasswordHash"]?.ToString();
                            adminFirstName = reader["FirstName"]?.ToString();
                            adminLastName = reader["LastName"]?.ToString();
                            adminRole = reader["Role"]?.ToString();
                            adminAccountStatus = Convert.ToInt32(reader["AccountStatus"]);
                        }
                    }
                }
            }

            if (adminId.HasValue)
            {
                // 🚫 Block login if admin account is inactive
                if (adminAccountStatus == 0)
                {
                    ViewBag.Error = "Your account has been deactivated. Please contact support.";
                    return View("~/Views/Authentication/Login.cshtml");
                }

                string hashedInputPassword = PasswordHelper.EncryptPassword(password);

                if (adminPasswordHash == hashedInputPassword)
                {
                    await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                    var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, $"{adminFirstName} {adminLastName}"),
            new Claim(ClaimTypes.Role, adminRole),
            new Claim("AdminId", adminId.ToString()),
            new Claim("AdminEmail", email)
        };

                    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                    var principal = new ClaimsPrincipal(identity);

                    await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

                    // Role-based landing page
                    if (string.Equals(adminRole, "IT_Admin", StringComparison.OrdinalIgnoreCase))
                    {
                        return RedirectToAction("Users_Management", "TenderAdmin");
                    }
                    else if (string.Equals(adminRole, "Vendor_Administrator", StringComparison.OrdinalIgnoreCase))
                    {
                        return RedirectToAction("Help", "Home");
                    }
                    else if (string.Equals(adminRole, "Tender_Administrator", StringComparison.OrdinalIgnoreCase))
                    {
                        return RedirectToAction("Index", "TenderAdmin");
                    }
                    else
                    {
                        // Fallback: generic dashboard or denied
                        return RedirectToAction("Index", "TenderAdmin");
                    }
                }
                else
                {
                    ViewBag.Error = "Invalid email or password";
                    return View("~/Views/Authentication/Login.cshtml");
                }
            }

            // ---------- 2. Attempt OVRS_USER (Legacy Supplier) login ----------
            int? legacyUserId = null;
            string legalName = null;
            string role = "OVRS_User";
            string supplierType = null;

            // Lookup supplier by email in the legacy Phase 1 database
            using (SqlConnection legacyConn = new SqlConnection(legacyConnString))
            {
                await legacyConn.OpenAsync();

                // Retrieve supplier details based on email
                using (SqlCommand cmd = new SqlCommand(
                    "SELECT user_id, legalname, [local/foreigner], email FROM tbl_suppliers WHERE email = @Email", legacyConn))
                {
                    cmd.Parameters.AddWithValue("@Email", email);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            object userIdObj = reader["user_id"];
                            if (userIdObj != DBNull.Value && userIdObj != null)
                            {
                                legacyUserId = Convert.ToInt32(userIdObj);
                            }
                            legalName = reader["legalname"] as string;

                            // Determine supplier type (local/foreigner) for claim enrichment
                            var localForeign = reader["local/foreigner"] as int? ?? Convert.ToInt32(reader["local/foreigner"]);
                            supplierType = localForeign == 1 ? "Local Supplier" : localForeign == 2 ? "Foreign Supplier" : "Unknown Supplier";
                        }
                    }
                }

                // If no matching supplier, authentication fails
                if (legacyUserId == null)
                {
                    ViewBag.Error = "Invalid email or password";
                    return View("~/Views/Authentication/Login.cshtml");
                }

                // Now, retrieve the user's password hash and personal details
                bool passwordMatch = false;
                string fullName = legalName ?? "";

                using (SqlCommand cmd = new SqlCommand(
                    "SELECT [password], [first_name], [last_name] FROM tbl_users WHERE user_id = @userId", legacyConn))
                {
                    cmd.Parameters.Add("@userId", SqlDbType.Int).Value = legacyUserId.Value;

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            var dbPassword = reader["password"]?.ToString();
                            string hashedInputPassword = PasswordHelper.EncryptPassword(password);

                            // Accept either hashed or plain (for test/legacy accounts)
                            if (dbPassword == hashedInputPassword || dbPassword == password)
                            {
                                passwordMatch = true;
                                fullName = $"{reader["first_name"]} {reader["last_name"]}".Trim();
                            }
                        }
                    }
                }

                // If password doesn't match, authentication fails
                if (!passwordMatch)
                {
                    ViewBag.Error = "Invalid email or password";
                    return View("~/Views/Authentication/Login.cshtml");
                }
            }

            // ---------- 3. Ensure OVRS_User is registered in Phase 2 DB and update role if necessary ----------
            using (SqlConnection defaultConn = new SqlConnection(defaultConnString))
            {
                await defaultConn.OpenAsync();

                using (SqlCommand cmd = new SqlCommand(
                    @"IF EXISTS (SELECT 1 FROM dbo.Users WHERE LegacyUserId = @LegacyUserId)
            BEGIN
                UPDATE dbo.Users 
                SET [Role] = @Role
                WHERE LegacyUserId = @LegacyUserId
            END
          ELSE
            BEGIN
                INSERT INTO dbo.Users ([Role], [LegacyUserId], [AccountStatus])
                VALUES (@Role, @LegacyUserId, 1) -- ✅ New users start with AccountStatus = 1 (Active)
            END", defaultConn))
                {
                    cmd.Parameters.Add("@LegacyUserId", SqlDbType.Int).Value = legacyUserId.Value;
                    cmd.Parameters.AddWithValue("@Role", role);
                    await cmd.ExecuteNonQueryAsync();
                }
            }


            // ---------- 4. Retrieve the user's Phase 2 UserId and AccountStatus for claim setup ----------
            int newUserId;
            int accountStatus;
            using (SqlConnection defaultConn = new SqlConnection(defaultConnString))
            {
                await defaultConn.OpenAsync();
                using (SqlCommand cmd = new SqlCommand(
                    "SELECT Id, AccountStatus FROM dbo.Users WHERE LegacyUserId = @LegacyUserId", defaultConn))
                {
                    cmd.Parameters.Add("@LegacyUserId", SqlDbType.Int).Value = legacyUserId.Value;

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            newUserId = Convert.ToInt32(reader["Id"]);
                            accountStatus = Convert.ToInt32(reader["AccountStatus"]);
                        }
                        else
                        {
                            ViewBag.Error = "Unable to locate your account. Please contact support.";
                            return View("~/Views/Authentication/Login.cshtml");
                        }
                    }
                }
            }

            // 🚫 If AccountStatus = 0, block login
            if (accountStatus == 0)
            {
                ViewBag.Error = "Your account has been deactivated. Please contact support.";
                return View("~/Views/Authentication/Login.cshtml");
            }


            // ---------- 5. Create authentication claims principal for the OVRS_User ----------
            var userClaims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, legalName ?? email), // User's full name or fallback to email
                new Claim(ClaimTypes.Role, role), // Set role to OVRS_User
                new Claim("UserId", newUserId.ToString()), // Internal Phase 2 UserId
                new Claim("SupplierType", supplierType ?? "Unknown Supplier") // Supplier type for UI/logic
            };
            var userIdentity = new ClaimsIdentity(userClaims, CookieAuthenticationDefaults.AuthenticationScheme);
            var userPrincipal = new ClaimsPrincipal(userIdentity);

            // Sign the user in with cookie authentication
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, userPrincipal);

            // Redirect OVRS_User to main dashboard
            return RedirectToAction("AllTenders", "OVRS_User");
        }
        [HttpPost]
        public async Task<IActionResult> ForgotPassword(string email)
        {
            if (string.IsNullOrEmpty(email))
                return Json(new { success = false, message = "Email is required" });

            string userType = null;

            // 1. Check Administrators table (Phase 2 DB)
            using (var conn = new SqlConnection(_config.GetConnectionString("DefaultConn")))
            {
                await conn.OpenAsync();
                using (var cmd = new SqlCommand("SELECT Id FROM Administrators WHERE Email=@Email", conn))
                {
                    cmd.Parameters.AddWithValue("@Email", email);
                    var exists = await cmd.ExecuteScalarAsync();
                    if (exists != null)
                        userType = "Admin";
                }
            }

            // 2. Check tbl_users table (Legacy DB)
            if (userType == null)
            {
                using (var conn = new SqlConnection(_config.GetConnectionString("LegacyDb")))
                {
                    await conn.OpenAsync();
                    using (var cmd = new SqlCommand("SELECT user_id FROM tbl_users WHERE email=@Email", conn))
                    {
                        cmd.Parameters.AddWithValue("@Email", email);
                        var exists = await cmd.ExecuteScalarAsync();
                        if (exists != null)
                            userType = "OvrUser";
                    }
                }
            }

            if (userType == null)
            {
                // Email not found
                return Json(new { success = false, message = "Email does not exist" });
            }

            // Generate token
            var token = Guid.NewGuid().ToString();
            var expiry = DateTime.UtcNow.AddHours(1);

            using (var conn = new SqlConnection(_config.GetConnectionString("DefaultConn")))
            {
                await conn.OpenAsync();
                using (var cmd = new SqlCommand(
                    "INSERT INTO PasswordResetTokens (Email, Token, ExpiryDate, UserType) VALUES (@Email,@Token,@ExpiryDate,@UserType)", conn))
                {
                    cmd.Parameters.AddWithValue("@Email", email);
                    cmd.Parameters.AddWithValue("@Token", token);
                    cmd.Parameters.AddWithValue("@ExpiryDate", expiry);
                    cmd.Parameters.AddWithValue("@UserType", userType);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // Send email with link
            var resetLink = Url.Action("ResetPassword", "Account", new { token = token }, Request.Scheme);
            var emailService = new EmailService(_config);
            await emailService.SendPasswordResetEmailAsync(email, resetLink);

            return Json(new { success = true, message = "Password reset link has been sent to your email." });
        }

        [HttpGet]
        public IActionResult ResetPassword(string token)
        {
            if (string.IsNullOrEmpty(token))
                return RedirectToAction("Login");

            // Look up the token in the DB to get the email
            string email = null;
            using (var conn = new SqlConnection(_config.GetConnectionString("DefaultConn")))
            {
                conn.Open();
                using (var cmd = new SqlCommand("SELECT Email FROM PasswordResetTokens WHERE Token=@Token", conn))
                {
                    cmd.Parameters.AddWithValue("@Token", token);
                    var result = cmd.ExecuteScalar();
                    if (result != null)
                        email = result.ToString();
                }
            }

            var model = new ResetPasswordViewModel { Token = token, Email = email };
            return View("~/Views/Authentication/ResetPassword.cshtml", model);
        }


        [HttpPost]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (!ModelState.IsValid)
                return View("~/Views/Authentication/ResetPassword.cshtml", model);

            if (model.NewPassword != model.ConfirmPassword)
            {
                ModelState.AddModelError("", "Passwords do not match.");
                return View("~/Views/Authentication/ResetPassword.cshtml", model);
            }

            // Validate password rules
            if (!IsValidPassword(model.NewPassword))
            {
                ModelState.AddModelError("", "Password does not meet security requirements.");
                return View("~/Views/Authentication/ResetPassword.cshtml", model);
            }

            // Hash the new password
            string newPasswordHash = PasswordHelper.EncryptPassword(model.NewPassword);

            // Get last 3 password hashes for this email
            List<string> lastHashes = new List<string>();
            using (var conn = new SqlConnection(_config.GetConnectionString("DefaultConn")))
            {
                await conn.OpenAsync();
                using (var cmd = new SqlCommand(
                    "SELECT TOP 3 PasswordHash FROM PasswordHistories WHERE Email=@Email ORDER BY ChangedAt DESC", conn))
                {
                    cmd.Parameters.AddWithValue("@Email", model.Email);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            lastHashes.Add(reader.GetString(0));
                        }
                    }
                }
            }

            // Check if new password matches any previous 3
            if (lastHashes.Any(h => h == newPasswordHash))
            {
                ModelState.AddModelError("", "You cannot reuse any of your last three passwords.");
                return View("~/Views/Authentication/ResetPassword.cshtml", model);
            }

            // Get token record
            PasswordResetToken tokenRecord = null;
            using (var conn = new SqlConnection(_config.GetConnectionString("DefaultConn")))
            {
                await conn.OpenAsync();
                using (var cmd = new SqlCommand("SELECT * FROM PasswordResetTokens WHERE Token=@Token", conn))
                {
                    cmd.Parameters.AddWithValue("@Token", model.Token);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            tokenRecord = new PasswordResetToken
                            {
                                Id = Convert.ToInt32(reader["Id"]),
                                Email = reader["Email"].ToString(),
                                ExpiryDate = Convert.ToDateTime(reader["ExpiryDate"]),
                                UserType = reader["UserType"].ToString()
                            };
                        }
                    }
                }
            }

            if (tokenRecord == null || tokenRecord.ExpiryDate < DateTime.UtcNow)
            {
                ModelState.AddModelError("", "Token is invalid or expired.");
                return View(model);
            }
            // Update user password
            if (tokenRecord.UserType == "Admin")
            {
                using (var conn = new SqlConnection(_config.GetConnectionString("DefaultConn")))
                {
                    await conn.OpenAsync();
                    using (var cmd = new SqlCommand("UPDATE Administrators SET PasswordHash=@Password WHERE Email=@Email", conn))
                    {
                        cmd.Parameters.AddWithValue("@Password", PasswordHelper.EncryptPassword(model.NewPassword));
                        cmd.Parameters.AddWithValue("@Email", tokenRecord.Email);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            else if (tokenRecord.UserType == "OvrUser")
            {
                using (var conn = new SqlConnection(_config.GetConnectionString("LegacyDb")))
                {
                    await conn.OpenAsync();
                    using (var cmd = new SqlCommand("UPDATE tbl_users SET password=@Password WHERE email=@Email", conn))
                    {
                        cmd.Parameters.AddWithValue("@Password", PasswordHelper.EncryptPassword(model.NewPassword));
                        cmd.Parameters.AddWithValue("@Email", tokenRecord.Email);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            // Log the password change in PasswordHistory
            using (var conn = new SqlConnection(_config.GetConnectionString("DefaultConn")))
            {
                await conn.OpenAsync();
                using (var cmd = new SqlCommand(
                    "INSERT INTO PasswordHistories (Email, PasswordHash, ChangedAt) VALUES (@Email, @PasswordHash, @ChangedAt)", conn))
                {
                    cmd.Parameters.AddWithValue("@Email", model.Email);
                    cmd.Parameters.AddWithValue("@PasswordHash", newPasswordHash);
                    cmd.Parameters.AddWithValue("@ChangedAt", DateTime.UtcNow);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            TempData["Success"] = "Password has been reset successfully. You can now log in.";
            return RedirectToAction("Login");
        }

        private bool IsValidPassword(string password)
        {
            if (password.Length < 8 || password.Length > 15) return false;
            if (!password.Any(char.IsUpper)) return false;
            if (!password.Any(char.IsLower)) return false;
            if (!password.Any(char.IsDigit)) return false;
            if (!password.Any(ch => !char.IsLetterOrDigit(ch))) return false;
            return true;
        }

        /// <summary>
        /// POST: /Account/Logout
        /// Logs out the current user and clears the authentication cookie.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            // Securely sign out the user from the authentication scheme
            await HttpContext.SignOutAsync(); // or SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
            // Redirect to public landing page or login
            return RedirectToAction("Index", "OVRS_User");
        }
    }
}