namespace AzurAssistant.Vision;

public readonly record struct PixelRegion(int X, int Y, int Width, int Height)
{
    public void Validate(int imageWidth, int imageHeight)
    {
        if (X < 0 || Y < 0 || Width <= 0 || Height <= 0
            || (long)X + Width > imageWidth || (long)Y + Height > imageHeight)
            throw new ArgumentOutOfRangeException(nameof(PixelRegion), "识别区域超出画面。");
    }
}

public sealed record TextRegion(string Text, double X, double Y, double Width, double Height, double Confidence = 1)
{
    // Optional scores aligned with Text's UTF-16 indices. Consumers must check alignment before slicing.
    // Confidence retains its original whole-block average for existing readers.
    public IReadOnlyList<float>? CharacterConfidences { get; init; }
}
