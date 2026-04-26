namespace VideoOrganizer.Import.DTOs;

public class ImportVideosDto
{
    public string VidFileName { get; set; } = string.Empty;
    public string VidLocalPath { get; set; } = string.Empty;
    public string VidMd5 { get; set; } = string.Empty;
    public int VidWidth { get; set; }
    public int VidHeight { get; set; }
    public double VidDurationInSeconds { get; set; }
    public List<string> AllTagsList { get; set; } = new();
}
