using System.ComponentModel.DataAnnotations.Schema;

namespace SightsAndSounds.Shared.Models
{
    [Table("Tracks")]
    public class Track
    {
        public Guid Id { get; set; }
        public required Song Song { get; set; }
        public List<string> Guests { get; set; } = new List<string>();
        public int Position { get; set; } = 0;
        public TimeSpan Length { get; set; }
        bool IsEncore { get; set; }
        bool IsAcoustic { get; set; }
        bool IsCover { get; set; }
        public bool IsMissing { get; set; }
        public string Notes { get; set; } = string.Empty;
    }
}
