using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace TUFHelperLite.Infrastructure.Downloads;

internal static class DownloadUrlPolicy
{
  public static Uri Validate(string value)
  {
    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
    {
      throw new InvalidOperationException("The download URL is invalid.");
    }

    Validate(uri);
    return uri;
  }

  public static void Validate(Uri uri)
  {
    if (uri == null ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
      throw new InvalidOperationException("Only HTTP and HTTPS download URLs are allowed.");
    }

    if (string.IsNullOrWhiteSpace(uri.Host) ||
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException("Local download hosts are not allowed.");
    }

    IPAddress[] addresses;
    try
    {
      addresses = Dns.GetHostAddresses(uri.DnsSafeHost);
    }
    catch (Exception e) when (e is SocketException || e is ArgumentException)
    {
      throw new InvalidOperationException("The download host could not be resolved.", e);
    }

    if (addresses.Length == 0 || addresses.Any(IsNonPublicAddress))
    {
      throw new InvalidOperationException("Private or local download hosts are not allowed.");
    }
  }

  private static bool IsNonPublicAddress(IPAddress address)
  {
    if (address.IsIPv4MappedToIPv6)
    {
      address = address.MapToIPv4();
    }

    if (address.AddressFamily == AddressFamily.InterNetworkV6)
    {
      byte[] bytes = address.GetAddressBytes();
      return IPAddress.IsLoopback(address) ||
             address.Equals(IPAddress.IPv6Any) ||
             address.Equals(IPAddress.IPv6None) ||
             address.IsIPv6LinkLocal ||
             address.IsIPv6SiteLocal ||
             address.IsIPv6Multicast ||
             (bytes[0] & 0xfe) == 0xfc;
    }

    if (address.AddressFamily != AddressFamily.InterNetwork)
    {
      return true;
    }

    byte[] octets = address.GetAddressBytes();
    return octets[0] == 0 ||
           octets[0] == 10 ||
           octets[0] == 127 ||
           (octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127) ||
           (octets[0] == 169 && octets[1] == 254) ||
           (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) ||
           (octets[0] == 192 && octets[1] == 168) ||
           (octets[0] == 198 && (octets[1] == 18 || octets[1] == 19)) ||
           octets[0] >= 224;
  }
}
