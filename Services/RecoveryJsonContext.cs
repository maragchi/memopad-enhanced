using System.Text.Json.Serialization;

namespace Memopad.Services;

/// <summary>RecoveryManifest の JSON 変換コード（コンパイル時に生成される）。</summary>
[JsonSerializable(typeof(RecoveryManifest))]
[JsonSerializable(typeof(RecoveryTabInfo))]
internal sealed partial class RecoveryJsonContext : JsonSerializerContext
{
}
