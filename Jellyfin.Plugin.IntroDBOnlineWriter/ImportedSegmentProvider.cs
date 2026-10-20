using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.IntroDBOnlineWriter;

/// <summary>
/// Provider registration makes the imported rows visible in Jellyfin's provider-filtered API.
/// It intentionally opts out of scans: data arrives exclusively through the admin endpoint.
/// </summary>
public sealed class ImportedSegmentProvider : IMediaSegmentProvider
{
    public const string ProviderName = "IntroDB Online Writer";
    public string Name => ProviderName;

    // Deliberately prevent full Media Segment Scans from using this provider.
    public ValueTask<bool> Supports(BaseItem item) => new(false);

    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<MediaSegmentDto>>(Array.Empty<MediaSegmentDto>());

    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
