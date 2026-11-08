using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BruTile;
using SkiaSharp;

namespace PathFinder.Mapping;

/// <summary>
/// One tile source made of several stacked ones: a base layer (e.g. satellite imagery) with
/// transparent overlays (e.g. roads and labels) drawn on top. Each tile is fetched from every
/// source in parallel and merged into a single image, so the map sees one ordinary layer.
/// All sources must share the base's tile schema.
/// </summary>
public sealed class CompositeTileSource : IHttpTileSource
{
    /// <summary>JPEG keeps merged tiles small and fast to encode; 90 keeps label text crisp.</summary>
    private const int JpegQuality = 90;

    private readonly IHttpTileSource _baseSource;
    private readonly IHttpTileSource[] _overlays;

    /// <param name="baseSource">The opaque bottom layer. If its tile fails, the whole tile fails.</param>
    /// <param name="overlays">Transparent layers drawn on top, in order. A failed overlay tile is skipped.</param>
    public CompositeTileSource(string name, IHttpTileSource baseSource, params IHttpTileSource[] overlays)
    {
        Name = name;
        _baseSource = baseSource;
        _overlays = overlays;
    }

    public string Name { get; }
    public ITileSchema Schema => _baseSource.Schema;

    // Empty on purpose: Mapsui would draw it as plain text over the map. MapView shows the
    // combined credit in its own overlay instead.
    public Attribution Attribution { get; } = new();

    public async Task<byte[]?> GetTileAsync(HttpClient httpClient, TileInfo tileInfo, CancellationToken? cancellationToken)
    {
        var baseTask = _baseSource.GetTileAsync(httpClient, tileInfo, cancellationToken);
        var overlayTasks = _overlays.Select(o => TryGetTile(o, httpClient, tileInfo, cancellationToken)).ToArray();

        var baseBytes = await baseTask;
        var overlayBytes = await Task.WhenAll(overlayTasks);
        if (baseBytes is null) return null;
        if (overlayBytes.All(b => b is null)) return baseBytes; // nothing to draw on top

        using var baseImage = SKBitmap.Decode(baseBytes);
        if (baseImage is null) return baseBytes; // not an image (e.g. an error page): pass it through unchanged

        using var surface = SKSurface.Create(new SKImageInfo(baseImage.Width, baseImage.Height));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(baseImage, 0, 0);
        foreach (var bytes in overlayBytes)
        {
            if (bytes is null) continue;
            using var overlay = SKBitmap.Decode(bytes);
            if (overlay is null) continue;
            // Overlays are drawn at the base tile's size, in case a provider serves a different resolution.
            canvas.DrawBitmap(overlay, new SKRect(0, 0, baseImage.Width, baseImage.Height));
        }

        using var merged = surface.Snapshot();
        using var encoded = merged.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return encoded.ToArray();
    }

    /// <summary>An overlay tile, or null if it couldn't be fetched (labels are optional; imagery isn't).</summary>
    private static async Task<byte[]?> TryGetTile(IHttpTileSource source, HttpClient httpClient, TileInfo tileInfo,
                                                  CancellationToken? cancellationToken)
    {
        try
        {
            return await source.GetTileAsync(httpClient, tileInfo, cancellationToken);
        }
        catch (Exception) when (cancellationToken?.IsCancellationRequested != true)
        {
            return null;
        }
    }
}
