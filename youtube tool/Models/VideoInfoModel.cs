using System;

namespace youtube_tool.Models
{
    public class VideoInfoModel
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Channel { get; set; } = string.Empty;
        public TimeSpan? Duration { get; set; }
        public DateTimeOffset? UploadDate { get; set; }
        public string ThumbnailUrl { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsPlaylist { get; set; }
    }
}
