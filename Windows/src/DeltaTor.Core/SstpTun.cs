using DeltaTor.Core.Sstp;

namespace DeltaTor.Core;

/// <summary>
/// Static facade the chain engine calls to obtain the platform adapter, the
/// exact counterpart of <see cref="TunnelEngine"/> for the SSTP data plane.
/// The app registers a factory at startup (a Wintun adapter in DeltaTor.Native,
/// mirroring Android's <c>VpnService.Builder</c>); null means the chain cannot
/// open at all, which <see cref="SstpChainEngine"/> reports as a failure.
/// </summary>
public static class SstpTun
{
    /// <summary>The adapter factory; the app sets it at startup. Null = unavailable.</summary>
    public static Func<ISstpTun?>? Factory { get; set; }
}
