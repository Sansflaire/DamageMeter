using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace DamageMeter.Panache;

public sealed class RenderSurface : IDisposable
{
    public int Width  { get; }
    public int Height { get; }

    private readonly SKSurface _surface;
    private bool _disposed;

    public RenderSurface(int width, int height)
    {
        Width  = width;
        Height = height;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("SkiaSharp: failed to create CPU surface.");
    }

    public SKCanvas Canvas => _surface.Canvas;

    public bool ReadPixels(byte[] destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var handle = GCHandle.Alloc(destination, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            return _surface.ReadPixels(info, handle.AddrOfPinnedObject(), Width * 4, 0, 0);
        }
        finally { handle.Free(); }
    }

    public byte[]? GetPngBytes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var snapshot = _surface.Snapshot();
        using var data     = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _surface.Dispose();
    }
}
