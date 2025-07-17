using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;
using System.Security.Claims;
using System.Text;
using System.Security.Cryptography;

namespace SABC_Phase2.Controllers
{
    public class AccountController : Controller
    {
        private readonly IConfiguration _config;

        public AccountController(IConfiguration config)
        {
            _config = config;
        }

        [HttpGet]
        public IActionResult Login()
        {
            // Always use explicit path!
            return View("~/Views/Authentication/Login.cshtml");
        }

        [HttpPost]
        public async Task<IActionResult> Login(string email, string password)
        {
            var legacyConnString = _config.GetConnectionString("LegacyDb");
            var defaultConnString = _config.GetConnectionString("DefaultConn");

            // 1. Try Admin login first
            int? adminId = null;
            string adminFirstName = null, adminLastName = null, adminRole = null, adminPasswordHash = null;

            using (var conn = new SqlConnection(defaultConnString))
            {
                await conn.OpenAsync();
                using (var cmd = new SqlCommand(
                    "SELECT Id, Email, PasswordHash, FirstName, LastName, Role FROM dbo.Administrators WHERE Email = @Email", conn))
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
                        }
                    }
                }
            }
            if (adminId.HasValue)
            {
                string hashedInputPassword = HashPassword(password);
                if (adminPasswordHash == hashedInputPassword)
                {
                    // Admin login success
                    await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                    var claims = new List<Claim>
                    {
                        new Claim(ClaimTypes.Name, $"{adminFirstName} {adminLastName}"),
                        new Claim(ClaimTypes.Role, "Administrator"),
                        new Claim("AdminId", adminId.ToString()),
                        new Claim("AdminEmail", email)
                    };
                    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                    var principal = new ClaimsPrincipal(identity);
                    await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

                    return RedirectToAction("Index", "TenderAdmin");
                }
                else
                {
                    // If admin email found but password incorrect, fail here
                    ViewBag.Error = "Invalid email or password";
                    return View("~/Views/Authentication/Login.cshtml");
                }
            }

            // 2. Try OVRS_User login
            int? legacyUserId = null;
            string legalName = null;
            string role = "OVRS_User";
            string supplierType = null;

            using (SqlConnection legacyConn = new SqlConnection(legacyConnString))
            {
                await legacyConn.OpenAsync();

                // Get supplier by email
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

                            var localForeign = reader["local/foreigner"] as int? ?? Convert.ToInt32(reader["local/foreigner"]);
                            supplierType = localForeign == 1 ? "Local Supplier" : localForeign == 2 ? "Foreign Supplier" : "Unknown Supplier";
                        }
                    }
                }

                if (legacyUserId == null)
                {
                    ViewBag.Error = "Invalid email or password";
                    return View("~/Views/Authentication/Login.cshtml");
                }

                // Get user by user_id and check password
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
                            if (dbPassword == password)
                            {
                                passwordMatch = true;
                                fullName = $"{reader["first_name"]} {reader["last_name"]}".Trim();
                            }
                        }
                    }
                }

                if (!passwordMatch)
                {
                    ViewBag.Error = "Invalid email or password";
                    return View("~/Views/Authentication/Login.cshtml");
                }
            }

            // 2. Insert or update user in SABC_Phase2.dbo.Users with role OVRS_User
            using (SqlConnection defaultConn = new SqlConnection(defaultConnString))
            {
                await defaultConn.OpenAsync();

                using (SqlCommand cmd = new SqlCommand(
                    @"IF EXISTS (SELECT 1 FROM dbo.Users WHERE LegacyUserId = @LegacyUserId)
                        UPDATE dbo.Users SET [Role] = @Role WHERE LegacyUserId = @LegacyUserId
                    ELSE
                        INSERT INTO dbo.Users ([Role], [LegacyUserId]) VALUES (@Role, @LegacyUserId)", defaultConn))
                {
                    cmd.Parameters.Add("@LegacyUserId", SqlDbType.Int).Value = legacyUserId.Value;
                    cmd.Parameters.AddWithValue("@Role", role);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // 3. Retrieve the new user's Id from SABC_Phase2.dbo.Users for claims
            int newUserId;
            using (SqlConnection defaultConn = new SqlConnection(defaultConnString))
            {
                await defaultConn.OpenAsync();
                using (SqlCommand cmd = new SqlCommand(
                    "SELECT Id FROM dbo.Users WHERE LegacyUserId = @LegacyUserId", defaultConn))
                {
                    cmd.Parameters.Add("@LegacyUserId", SqlDbType.Int).Value = legacyUserId.Value;
                    var result = await cmd.ExecuteScalarAsync();
                    newUserId = Convert.ToInt32(result);
                }
            }

            // 4. Set up the claims and sign in as OVRS_User
            var userClaims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, legalName ?? email),
                new Claim(ClaimTypes.Role, role),
                new Claim("UserId", newUserId.ToString()),
                new Claim("SupplierType", supplierType ?? "Unknown Supplier")
            };
            var userIdentity = new ClaimsIdentity(userClaims, CookieAuthenticationDefaults.AuthenticationScheme);
            var userPrincipal = new ClaimsPrincipal(userIdentity);

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, userPrincipal);

            return RedirectToAction("AllTenders", "OVRS_User");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(); // or SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
            return RedirectToAction("Index", "OVRS_User");
        }

        private string HashPassword(string password)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(password);
                var hash = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }
    }
}