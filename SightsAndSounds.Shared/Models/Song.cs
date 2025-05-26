using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SightsAndSounds.Shared.Models
{
    public class Song
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<string> AlternateNames { get; set; } = new List<string>();
        public string Album { get; set; } = string.Empty;
        public long Playcount { get; set; } = 0;
        public DateTime? LiveDebut { get; set; }
        public string Notes { get; set; } = string.Empty;
    }
}
