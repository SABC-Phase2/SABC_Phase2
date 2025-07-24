using NodaTime;

namespace SABC_Phase2.Services
{
    public class SouthAfricanTimeService
    {
        private static readonly string SouthAfricaZoneId = "Africa/Johannesburg";
        private readonly DateTimeZone _saZone;

        public SouthAfricanTimeService()
        {
            // Load the South African time zone from the built-in tz database
            _saZone = DateTimeZoneProviders.Tzdb[SouthAfricaZoneId];
        }

        /// <summary>
        /// Gets the current time in South Africa Standard Time (SAST)
        /// </summary>
        public LocalDateTime GetCurrentSouthAfricanTime()
        {
            var nowUtc = SystemClock.Instance.GetCurrentInstant();
            var saZoned = nowUtc.InZone(_saZone);
            return saZoned.LocalDateTime;
        }

        /// <summary>
        /// Converts a LocalDateTime in South Africa time to UTC Instant
        /// </summary>
        public Instant ConvertSaLocalToUtc(LocalDateTime saLocal)
        {
            var saZoned = saLocal.InZoneLeniently(_saZone);
            return saZoned.ToInstant();
        }

        /// <summary>
        /// Converts UTC to South Africa LocalDateTime
        /// </summary>
        public LocalDateTime ConvertUtcToSaLocal(DateTime utcDateTime)
        {
            var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc));
            var saZoned = instant.InZone(_saZone);
            return saZoned.LocalDateTime;
        }
    }
}
