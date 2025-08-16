using System.ComponentModel.DataAnnotations.Schema;

namespace SightsAndSounds.Shared.Models
{
    [Table("Venues")]
    public class Venue
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public VenueType Type { get; set; } = VenueType.Unknown;
        public List<string> AlternateNames { get; set; } = new List<string>();
        public string Notes { get; set; } = string.Empty;
        public int DmbAlmanacId { get; set; } = -1;
        public string DmbAlmanacUrl { get; set; } = string.Empty;

        public void SetVenueTypeFromString(string? raw) => Type = MapVenueType(raw);
        public static VenueType MapVenueType(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return VenueType.Unknown;
            }
            var venueTypeString = raw.Trim();

            return venueTypeString switch
            {
                _ when venueTypeString.Contains("Stadium", StringComparison.OrdinalIgnoreCase)
                    => VenueType.Stadium,

                _ when venueTypeString.Contains("Amphitheater", StringComparison.OrdinalIgnoreCase)
                    => VenueType.Amphitheater,

                _ when venueTypeString.Contains("Auditorium", StringComparison.OrdinalIgnoreCase)
                => VenueType.Auditorium,

                _ when venueTypeString.Contains("Festival", StringComparison.OrdinalIgnoreCase)
                => VenueType.Festival,

                _ when venueTypeString.Contains("Arena", StringComparison.OrdinalIgnoreCase)
                => VenueType.Arena,

                _ when venueTypeString.Contains("Theater", StringComparison.OrdinalIgnoreCase)
                    || venueTypeString.Contains("Theatre", StringComparison.OrdinalIgnoreCase)
                => VenueType.Theater,

                _ when venueTypeString.Contains("Club", StringComparison.OrdinalIgnoreCase)
                    || venueTypeString.Contains("Bar", StringComparison.OrdinalIgnoreCase)
                => VenueType.Club,

                _ when venueTypeString.Contains("Frat", StringComparison.OrdinalIgnoreCase)
                    || venueTypeString.Contains("Fraternity", StringComparison.OrdinalIgnoreCase)
                => VenueType.FratHouse,

                _ => VenueType.Unknown
            };
        }
    }

    public enum VenueType
    {
        Stadium,
        Amphitheater,
        Auditorium,
        Festival,
        Arena,
        Theater,
        Club,
        FratHouse,
        Unknown
    }

    
}
