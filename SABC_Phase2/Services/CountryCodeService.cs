using System.Text.Json;
using System.Net.Http;

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
                var response = await _httpClient.GetAsync("https://restcountries.com/v3.1/all?fields=name,idd,flags,cca2");
                response.EnsureSuccessStatusCode();

                var jsonContent = await response.Content.ReadAsStringAsync();
                var countries = JsonSerializer.Deserialize<List<RestCountryResponse>>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var countryCodes = new List<CountryCode>();

                foreach (var country in countries)
                {
                    if (country.Idd?.Root != null && country.Idd.Suffixes?.Any() == true && !string.IsNullOrWhiteSpace(country.Cca2))
                    {
                        foreach (var suffix in country.Idd.Suffixes)
                        {
                            var dialingCode = country.Idd.Root + suffix;
                            countryCodes.Add(new CountryCode
                            {
                                CountryName = country.Name.Common,
                                DialingCode = dialingCode,
                                IsoCode = country.Cca2,
                                // Point to your local flag file
                                FlagUrl = $"/lib/flags/{country.Cca2.ToLower()}.png"
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
                new CountryCode { CountryName = "South Africa", DialingCode = "+27", IsoCode = "ZA", FlagUrl = "/lib/flags/za.png" },
                new CountryCode { CountryName = "United States", DialingCode = "+1", IsoCode = "US", FlagUrl = "/lib/flags/us.png" },
                // Add other fallback countries as needed
            };
        }
    }

    public class CountryCode
    {
        public string CountryName { get; set; }
        public string DialingCode { get; set; }
        public string FlagUrl { get; set; }
        public string IsoCode { get; set; }
    }

    // API Response models
    public class RestCountryResponse
    {
        public CountryName Name { get; set; }
        public Idd Idd { get; set; }
        public Flags Flags { get; set; }
        public string Cca2 { get; set; } // <-- Add this for ISO code
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

    public class Flags
    {
        public string Png { get; set; }
        public string Svg { get; set; }
    }
}