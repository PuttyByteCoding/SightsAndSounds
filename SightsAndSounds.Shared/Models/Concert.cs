using System;
using System.Collections.Generic;
using System.Diagnostics.SymbolStore;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SightsAndSounds.Shared.Models
{
    public class Concert
    {
        public Guid Id { get; set; }
        public DateTime Date { get; set; }
        public Venue? Venue { get; set; }
        public RecordingType RecordingType { get; set; }
        public List<Track> Setlist { get; set; } = new List<Track>();
        public string Description { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
    }


    public enum RecordingType
    {
        Mic,
        Soundboard,
        FM,
        XM,
        TV,
    }
}
