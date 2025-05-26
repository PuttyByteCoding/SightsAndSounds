using Microsoft.AspNetCore.Mvc;
using SightsAndSounds.Shared.Models;

[ApiController]
[Route("api/[controller]")]
public class MediaController : ControllerBase
{
    [HttpGet("concert/{concertId}/track/{trackId}")]
    public IActionResult GetTrackFile(Guid concertId, Guid trackId)
    {
        // Lookup track in DB, get FileLocation
        var track = new Track(); // TODO:  fetch from DB
        if (track == null || !System.IO.File.Exists(track.TrackFileLocation))
            return NotFound();

        var mimeType = "audio/mpeg"; // TODO: detect based on file extension
        return PhysicalFile(track.TrackFileLocation, mimeType);
    }
}