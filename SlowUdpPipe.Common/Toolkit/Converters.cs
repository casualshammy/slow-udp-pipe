﻿namespace SlowUdpPipe.Common.Toolkit;

public static class Converters
{
  public static string BytesToString(ulong _bytesPerSecond)
  {
    var bitsPerSecond = (double)_bytesPerSecond * 8;

    if (bitsPerSecond > (ulong)1024 * 1024 * 1024 * 1024)
      return $"{bitsPerSecond / ((ulong)1024 * 1024 * 1024 * 1024):F2} Tbps";
    if (bitsPerSecond > 1024 * 1024 * 1024)
      return $"{bitsPerSecond / (1024 * 1024 * 1024):F2} Gbps";
    if (bitsPerSecond > 1024 * 1024)
      return $"{bitsPerSecond / (1024 * 1024):F2} Mbps";
    if (bitsPerSecond > 1024)
      return $"{bitsPerSecond / 1024:F2} Kbps";

    return $"{bitsPerSecond:F2} bps";
  }

}
