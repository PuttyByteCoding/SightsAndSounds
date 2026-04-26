using VideoOrganizer.Domain.Models;

namespace VideoOrganizer.Import.Services;

public static class CodecHelper
{
    public static VideoCodec GetVideoCodec(string codecDesc)
    {
        return codecDesc.ToLower() switch
        {
            "hevc" => VideoCodec.HEVC,
            "h264" => VideoCodec.H264,
            "h265" => VideoCodec.H265,
            _ => VideoCodec.Other
        };
    }
}
