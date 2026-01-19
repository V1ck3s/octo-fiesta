namespace octo_fiesta.Models.Download;

/// <summary>
/// Information about an ongoing or completed download
/// </summary>
public class DownloadInfo
{
    public string SongId { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string ExternalProvider { get; set; } = string.Empty;
    public DownloadStatus Status { get; set; }
    public double Progress { get; set; } // 0.0 to 1.0
    public string? LocalPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    
    /// <summary>
    /// Path to backup file during quality upgrade (used to restore on failure)
    /// </summary>
    public string? BackupPath { get; set; }
}
