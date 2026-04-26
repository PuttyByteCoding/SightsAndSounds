namespace VideoOrganizer.Shared.Dto;

public enum CameraTypes
{
    Unknown,
    CellPhone,
    Camcorder,
    ProfessionalCamera,
    NotChecked
}

public enum VideoQuality
{
    SingleCamera,
    MultipleCameras,
    LowQuality,
    NotChecked
}

public enum VideoDimensionFormat
{
    UHD8k,
    UHD4K,
    HD1080p,
    HD720p,
    SD576p4x3,
    SD576p16x9,
    SD480p4x3,
    SD480p16x9,
    VerticalUHD4k,
    Vertical1080p,
    Vertical720p,
    NonStandard
}

public enum VideoCodec
{
    H264,
    H265,
    HEVC,
    NotChecked,
    Other
}

public enum VideoBlockTypes
{
    Clip,  // A noteworthy region — can be exported as its own Video.
    Hide,  // Skipped during playback; removed when "download with hides removed" is used.
    Other
}