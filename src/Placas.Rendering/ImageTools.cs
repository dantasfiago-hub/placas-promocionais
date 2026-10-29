using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Placas.Rendering;

public static class ImageTools
{
    public const int MaxSide = 1000;
    public const int ThumbnailSide = 96;

    // Reduz a imagem e a regrava (PNG se houver transparência, JPEG se for opaca) para o banco não crescer demais.
    public static (byte[] Data, byte[] Thumbnail) Prepare(byte[] source)
    {
        var frame = Decode(source);
        return (Encode(Fit(frame, MaxSide)), Encode(Fit(frame, ThumbnailSide)));
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidOperationException("O arquivo não tem imagem.");
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static BitmapSource Fit(BitmapSource source, int maxSide)
    {
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(source.PixelWidth, source.PixelHeight));
        if (scale >= 1) return source;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static byte[] Encode(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] < 255) { hasAlpha = true; break; }
        BitmapEncoder encoder;
        BitmapSource frame;
        if (hasAlpha) { encoder = new PngBitmapEncoder(); frame = bgra; }
        else
        {
            encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            var opaque = new FormatConvertedBitmap(bgra, PixelFormats.Bgr32, null, 0);
            opaque.Freeze();
            frame = opaque;
        }
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
