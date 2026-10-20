using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroDBOnlineWriter.Api;

public sealed class ImportSegment
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("start_ticks")]
    public long StartTicks { get; set; }

    [JsonPropertyName("end_ticks")]
    public long EndTicks { get; set; }
}

public sealed class ImportRecord
{
    [JsonPropertyName("item_id")]
    public string? ItemId { get; set; }

    [JsonPropertyName("segments")]
    public List<ImportSegment>? Segments { get; set; }
}

public sealed class ImportBatch
{
    [JsonPropertyName("records")]
    public List<ImportRecord>? Records { get; set; }
}

[ApiController]
[Route("Plugins/IntroDBOnlineWriter")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class OnlineSegmentsController : ControllerBase
{
    private const int MaxEpisodes = 100;
    private const int MaxSegments = 500;
    private const long MaximumUnknownDurationTicks = 8L * 3600 * TimeSpan.TicksPerSecond;
    private const long DurationToleranceTicks = 3L * TimeSpan.TicksPerSecond;
    private static readonly SemaphoreSlim ImportLock = new(1, 1);

    private readonly IMediaSegmentManager _segments;
    private readonly ILibraryManager _library;
    private readonly ILogger<OnlineSegmentsController> _log;

    public OnlineSegmentsController(
        IMediaSegmentManager segments,
        ILibraryManager library,
        ILogger<OnlineSegmentsController> log)
    {
        _segments = segments;
        _library = library;
        _log = log;
    }

    [HttpGet("Status")]
    public IActionResult Status() => Ok(new
    {
        Plugin = "IntroDB Online Writer",
        Version = "0.1.0",
        Mode = "manual-online-import-only",
        ProviderName = ImportedSegmentProvider.ProviderName,
        ProviderId = ProviderId(),
        MaxEpisodesPerRequest = MaxEpisodes,
        MaxSegmentsPerRequest = MaxSegments
    });

    [HttpPost("Import")]
    [RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> Import(
        [FromBody] ImportBatch? batch,
        [FromQuery] bool dryRun,
        CancellationToken cancellationToken)
    {
        if (batch?.Records is not { Count: > 0 and <= MaxEpisodes } records)
        {
            return BadRequest(new { error = $"Provide 1-{MaxEpisodes} episode records." });
        }

        if (records.Sum(x => x.Segments?.Count ?? 0) > MaxSegments)
        {
            return BadRequest(new { error = $"Limit is {MaxSegments} segments per request." });
        }

        await ImportLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Strictly validate ALL records before performing any write.
            var checkedRows = new List<(Guid ItemId, List<(MediaSegmentType Type, long Start, long End)> Segments)>();
            var seenIds = new HashSet<Guid>();
            foreach (var record in records)
            {
                if (!Guid.TryParse(record.ItemId, out var itemId) || itemId == Guid.Empty)
                {
                    return BadRequest(new { error = "Invalid item_id." });
                }

                if (!seenIds.Add(itemId))
                {
                    return BadRequest(new { error = "Duplicate episode ID in one request.", itemId });
                }

                if (_library.GetItemById(itemId) is not Episode episode)
                {
                    return BadRequest(new { error = "Item does not exist or is not a TV episode.", itemId });
                }

                if (record.Segments is not { Count: > 0 } input)
                {
                    return BadRequest(new { error = "Episode must contain segments.", itemId });
                }

                var parsed = new List<(MediaSegmentType Type, long Start, long End)>();
                foreach (var seg in input)
                {
                    if (seg.Type is null ||
                        !Enum.TryParse<MediaSegmentType>(seg.Type, true, out var type) ||
                        (type != MediaSegmentType.Intro && type != MediaSegmentType.Outro && type != MediaSegmentType.Recap))
                    {
                        return BadRequest(new { error = "Unsupported segment type; Intro, Outro, Recap only.", itemId });
                    }

                    if (seg.StartTicks < 0 || seg.EndTicks <= seg.StartTicks ||
                        seg.EndTicks > MaximumUnknownDurationTicks ||
                        (episode.RunTimeTicks is long runtime && runtime > 0 &&
                         seg.EndTicks > runtime + DurationToleranceTicks))
                    {
                        return BadRequest(new { error = "Invalid or out-of-runtime timestamps.", itemId });
                    }

                    parsed.Add((type, seg.StartTicks, seg.EndTicks));
                }

                // Duplicate ranges and conflicting intro/outro time ranges are quarantined upstream.
                if (parsed.Distinct().Count() != parsed.Count)
                {
                    return BadRequest(new { error = "Duplicate segment range.", itemId });
                }

                for (var i = 0; i < parsed.Count; i++)
                {
                    for (var j = i + 1; j < parsed.Count; j++)
                    {
                        var first = parsed[i];
                        var second = parsed[j];
                        var isIntroOutro =
                            (first.Type == MediaSegmentType.Intro && second.Type == MediaSegmentType.Outro) ||
                            (first.Type == MediaSegmentType.Outro && second.Type == MediaSegmentType.Intro);
                        if (!isIntroOutro)
                        {
                            continue;
                        }

                        var overlap = Math.Min(first.End, second.End) - Math.Max(first.Start, second.Start);
                        if (overlap >= TimeSpan.TicksPerSecond)
                        {
                            return BadRequest(new { error = "Overlapping intro/outro intervals.", itemId });
                        }
                    }
                }

                checkedRows.Add((itemId, parsed));
            }

            var pending = checkedRows.Where(r => !_segments.HasSegments(r.ItemId)).ToList();
            var skipped = checkedRows.Count - pending.Count;

            if (dryRun)
            {
                return Ok(new
                {
                    DryRun = true,
                    EpisodesReceived = checkedRows.Count,
                    EpisodesToInsert = pending.Count,
                    AlreadyHaveSegments = skipped,
                    SegmentsToInsert = pending.Sum(x => x.Segments.Count)
                });
            }

            // Jellyfin persists each segment individually. Retrying is idempotent at episode level;
            // this is not an atomic multi-episode transaction.
            var insertedSegments = 0;
            var insertedEpisodes = 0;
            foreach (var row in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_segments.HasSegments(row.ItemId))
                {
                    skipped++;
                    continue;
                }

                var createdInEpisode = new List<Guid>();
                try
                {
                    foreach (var (type, start, end) in row.Segments)
                    {
                        var segmentId = Guid.NewGuid();
                        await _segments.CreateSegmentAsync(new MediaSegmentDto
                        {
                            Id = segmentId,
                            ItemId = row.ItemId,
                            Type = type,
                            StartTicks = start,
                            EndTicks = end
                        }, ProviderId()).ConfigureAwait(false);
                        createdInEpisode.Add(segmentId);
                    }
                }
                catch
                {
                    // Best-effort rollback only for this episode's segments if a write failed.
                    foreach (var segmentId in createdInEpisode)
                    {
                        try
                        {
                            await _segments.DeleteSegmentAsync(segmentId).ConfigureAwait(false);
                        }
                        catch (Exception rollbackError)
                        {
                            _log.LogError(rollbackError, "Failed to roll back imported segment {SegmentId}", segmentId);
                        }
                    }
                    throw;
                }

                insertedSegments += createdInEpisode.Count;
                insertedEpisodes++;
            }

            _log.LogInformation("IntroDB Online Writer imported {Segments} segments into {Episodes} episodes; skipped {Skipped} existing episodes",
                insertedSegments, insertedEpisodes, skipped);

            return Ok(new
            {
                DryRun = false,
                EpisodesReceived = checkedRows.Count,
                EpisodesInserted = insertedEpisodes,
                SegmentsInserted = insertedSegments,
                AlreadyHaveSegments = skipped
            });
        }
        finally
        {
            ImportLock.Release();
        }
    }

    private static string ProviderId()
    {
        var bytes = MD5.HashData(Encoding.Unicode.GetBytes(ImportedSegmentProvider.ProviderName.ToLowerInvariant()));
        return new Guid(bytes).ToString("N");
    }
}
