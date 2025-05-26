namespace SightsAndSounds.Shared.Models
{
    public class Venue
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public VenueType Type { get; set; }
        public List<string> AlternateNames { get; set; } = new List<string>();
        public string Notes { get; set; } = string.Empty;
    }

    public enum VenueType
    {
        Stadium,
        Amphitheater,
        Festival,
        Arena,
        Theater,
        Club,
        FratHouse,
        Unknown
    }
}
