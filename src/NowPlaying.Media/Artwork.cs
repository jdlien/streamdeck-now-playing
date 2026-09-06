namespace NowPlaying.Media;

/// <summary>
/// The chosen session's thumbnail, as the player supplied it (usually JPEG
/// or PNG, a few hundred pixels square). Kept out of <see cref="NowPlayingSnapshot"/>
/// so snapshot equality stays cheap; <see cref="Key"/> is a content hash that
/// changes when the image does.
/// </summary>
/// <param name="Key">Hex SHA-1 of <paramref name="Bytes"/>.</param>
/// <param name="Bytes">The encoded image.</param>
/// <param name="ContentType">MIME type as reported by the stream, if any.</param>
public sealed record Artwork(string Key, byte[] Bytes, string? ContentType);
