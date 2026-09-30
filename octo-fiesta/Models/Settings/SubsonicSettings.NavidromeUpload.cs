namespace octo_fiesta.Models.Settings;

public partial class SubsonicSettings
{
    public bool UseNavidromeUploadApi { get; set; }

    public int NavidromeLibraryId { get; set; } = 1;

    public string? NavidromeUploadFolder { get; set; }
}
