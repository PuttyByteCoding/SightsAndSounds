namespace SightsAndSounds.Shared.Models
{
    public class Track
    {
        // TODO: Why does Visual Studio complain about this property?
        //public required Song Song { get; set; }
        public List<string> Guests { get; set; }
        public int Position { get; set; }
        bool IsEncore { get; set; }
        bool IsAcoustic { get; set; }
    }
}
