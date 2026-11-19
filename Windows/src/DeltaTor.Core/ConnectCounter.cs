namespace DeltaTor.Core;

/// <summary>
/// Counts connections by downloading a one byte asset from a dedicated release
/// once per connect the user asks for.
///
/// GitHub increments the download counter of a release asset on every request,
/// so the asset's download count is the number of connections. The asset is
/// read into memory and discarded immediately -- never written to disk -- so
/// there is no file to clean up and no storage is consumed.
///
/// Callers wrap this in <c>Task.Run</c>: the request must never delay the
/// actual connection. Retries live in <see cref="StatBeacon"/> and happen only
/// when the previous request really failed, which is what keeps one connection
/// from counting twice.
/// </summary>
public static class ConnectCounter
{
    private const string TAG = "ConnectCounter";

    private const string AssetUrl =
        "https://github.com/Delta-Kronecker/ForInstallationStatistics/releases/download/" +
        "connected/connected-windows";

    public static void CountConnect() => StatBeacon.Hit(TAG, AssetUrl, "DeltaTor-Windows");
}