using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace DamageMeter.Panache;

public sealed class TextureManager : IDisposable
{
    private readonly ITextureProvider _texProvider;
    private IDalamudTextureWrap? _texture;
    private byte[]? _pixelBuffer;
    private bool _disposed;

    public TextureManager(ITextureProvider texProvider)
    {
        _texProvider = texProvider;
    }

    public ImTextureID? Upload(RenderSurface surface)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int byteCount = surface.Width * surface.Height * 4;
        if (_pixelBuffer == null || _pixelBuffer.Length != byteCount)
            _pixelBuffer = new byte[byteCount];
        if (!surface.ReadPixels(_pixelBuffer)) return null;
        _texture?.Dispose();
        _texture = null;
        var spec = RawImageSpecification.Rgba32(surface.Width, surface.Height);
        _texture  = _texProvider.CreateFromRaw(spec, _pixelBuffer.AsSpan(), "DamageMeter.MeterCanvas");
        return _texture?.Handle;
    }

    public ImTextureID? Handle => _texture?.Handle;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _texture?.Dispose();
        _texture = null;
    }
}
