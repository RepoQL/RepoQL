using SkiaSharp;
using Svg.Skia;

namespace RepoQL.Sandbox;

/// <summary>
/// Purpose: Rasterize SVG to PNG using SkiaSharp (cross-platform, system fonts).
/// Complexity: Stateless — each call parses SVG, renders to a Skia surface, encodes to PNG.
/// Replaces the WASI-based ResvgRenderer which couldn't load fonts under WASI.
/// </summary>
public sealed class SvgRenderer
{
    /// <summary>
    /// Render SVG string to PNG bytes.
    /// </summary>
    /// <param name="svg">SVG content string.</param>
    /// <param name="scale">Scale factor (1.0 = native size, 2.0 = 2x).</param>
    /// <returns>PNG image bytes.</returns>
    public byte[] RenderToPng(string svg, float scale = 1.0f)
    {
        using var skSvg = new SKSvg();
        skSvg.FromSvg(svg);

        if (skSvg.Picture is null)
            throw new InvalidOperationException("Failed to parse SVG");

        var bounds = skSvg.Picture.CullRect;
        var width = (int)(bounds.Width * scale);
        var height = (int)(bounds.Height * scale);

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("SVG has zero dimensions");

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;

        // White background
        canvas.Clear(SKColors.White);

        // Scale and render
        if (Math.Abs(scale - 1.0f) > 0.001f)
            canvas.Scale(scale);

        canvas.DrawPicture(skSvg.Picture);

        // Encode to PNG
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);

        return data.ToArray();
    }
}
