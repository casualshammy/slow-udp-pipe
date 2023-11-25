﻿using Ax.Fw.Extensions;
using Ax.Fw.JsonStorages;
using Ax.Fw.SharedTypes.Interfaces;
using SlowUdpPipe.Interfaces;
using SlowUdpPipe.Server.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SlowUdpPipe.Modules.SettingsProvider;

internal class SettingsProviderImpl : ISettingsProvider
{
  public SettingsProviderImpl(CommandLineOptions _options, IReadOnlyLifetime _lifetime)
  {
    if (_options.ConfigFilePath.IsNullOrWhiteSpace())
      throw new InvalidDataException("Path to config file is empty!");

    _ = File.ReadAllBytes(_options.ConfigFilePath);

    var lifetime = _lifetime.GetChildLifetime();
    if (lifetime == null)
      throw new InvalidOperationException($"Lifetime is already ended");

    var jsonOptions = new JsonSerializerOptions()
    {
      PropertyNameCaseInsensitive = true,
      AllowTrailingCommas = true,
    };
    var jsonCtx = new ConfigFileJsonSerializationContext(jsonOptions);

    var configStorage = new JsonStorage<IReadOnlyDictionary<string, UdpTunnelServerRawOptions>>(
      _options.ConfigFilePath,
      jsonCtx.IReadOnlyDictionaryStringUdpTunnelServerRawOptions,
      lifetime);

    Definitions = configStorage;
  }

  public IObservable<IReadOnlyDictionary<string, UdpTunnelServerRawOptions>?> Definitions { get; }

}

[JsonSerializable(typeof(IReadOnlyDictionary<string, UdpTunnelServerRawOptions>))]
internal partial class ConfigFileJsonSerializationContext : JsonSerializerContext
{

}