namespace Memopad.Services;

public sealed record RecoveryTabInfo(string? FilePath, string UntitledName, TextEncodingKind Encoding, LineEndingKind LineEnding, bool IsDirty, string? ContentFile, int CaretPosition);
