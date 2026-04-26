using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Import.Services;

public static class VideoDimensionFormatHelper
{
    public static VideoDimensionFormat GetDimensionFormat(int height, int width)
    {
        return (height, width) switch
        {
            (4320, 7680) => VideoDimensionFormat.UHD8k,
            (2160, 3840) => VideoDimensionFormat.UHD4K,
            (3840, 2160) => VideoDimensionFormat.VerticalUHD4k,
            (1080, 1920) => VideoDimensionFormat.HD1080p,
            (1920, 1080) => VideoDimensionFormat.Vertical1080p,
            (720, 1280) => VideoDimensionFormat.HD720p,
            (1280, 720) => VideoDimensionFormat.Vertical720p,
            (576, 768) => VideoDimensionFormat.SD576p4x3,
            (576, 1024) => VideoDimensionFormat.SD576p16x9,
            (480, 640) => VideoDimensionFormat.SD480p4x3,
            (480, 854) => VideoDimensionFormat.SD480p16x9,
            _ => VideoDimensionFormat.NonStandard
        };
    }
}
