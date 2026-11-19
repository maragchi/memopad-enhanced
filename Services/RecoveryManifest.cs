using System.Collections.Generic;

namespace Memopad.Services;

public sealed record RecoveryManifest(int ActiveIndex, List<RecoveryTabInfo> Tabs);
