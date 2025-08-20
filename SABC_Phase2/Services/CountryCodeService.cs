using System.Text.Json;
using System.Net.Http; // Add this using directive

namespace SABC_Phase2.Services
{
    public class CountryCodeService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<CountryCodeService> _logger;

        public CountryCodeService(HttpClient httpClient, ILogger<CountryCodeService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<List<CountryCode>> GetCountryCodesAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync("https://restcountries.com/v3.1/all?fields=name,idd");
                response.EnsureSuccessStatusCode();

                // Use the Content property to read the string
                var jsonContent = await response.Content.ReadAsStringAsync();
                var countries = JsonSerializer.Deserialize<List<RestCountryResponse>>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var countryCodes = new List<CountryCode>();

                foreach (var country in countries)
                {
                    if (country.Idd?.Root != null && country.Idd.Suffixes?.Any() == true)
                    {
                        foreach (var suffix in country.Idd.Suffixes)
                        {
                            var dialingCode = country.Idd.Root + suffix;
                            countryCodes.Add(new CountryCode
                            {
                                CountryName = country.Name.Common,
                                DialingCode = dialingCode
                            });
                        }
                    }
                }

                return countryCodes
                    .OrderBy(c => c.CountryName)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching country codes from API");

                // Fallback to hardcoded values if API fails
                return GetFallbackCountryCodes();
            }
        }

        private List<CountryCode> GetFallbackCountryCodes()
        {
            return new List<CountryCode>
            {
                new CountryCode { CountryName = "South Africa", DialingCode = "+27" },
                new CountryCode { CountryName = "United States", DialingCode = "+1" },
                new CountryCode { CountryName = "United Kingdom", DialingCode = "+44" },
                new CountryCode { CountryName = "India", DialingCode = "+91" },
                new CountryCode { CountryName = "Australia", DialingCode = "+61" },
                new CountryCode { CountryName = "Germany", DialingCode = "+49" },
                new CountryCode { CountryName = "France", DialingCode = "+33" },
                new CountryCode { CountryName = "Canada", DialingCode = "+1" }
            };
        }
    }

    public class CountryCode
    {
        public string CountryName { get; set; }
        public string DialingCode { get; set; }
    }

    // API Response models
    public class RestCountryResponse
    {
        public CountryName Name { get; set; }
        public Idd Idd { get; set; }
    }

    public class CountryName
    {
        public string Common { get; set; }
        public string Official { get; set; }
    }

    public class Idd
    {
        public string Root { get; set; }
        public string[] Suffixes { get; set; }
    }
}