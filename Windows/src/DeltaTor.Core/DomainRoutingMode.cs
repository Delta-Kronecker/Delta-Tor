namespace DeltaTor.Core;

/// <summary>
/// Domain routing mode for geo-bypass / split routing decisions.
/// BYPASS: listed domains leave the tunnel and go direct.
/// ONLY_VPN: only listed domains use the tunnel (everything else goes direct).
/// </summary>
public enum DomainRoutingMode
{
    Bypass,
    OnlyVpn
}
