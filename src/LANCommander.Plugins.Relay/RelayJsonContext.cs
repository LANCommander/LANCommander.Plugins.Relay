using System.Text.Json.Serialization;
using LANCommander.Plugins.Relay.Authentication;
using LANCommander.Plugins.Relay.Storage;

namespace LANCommander.Plugins.Relay;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RelayPluginSettings))]
internal sealed partial class RelayJsonContext : JsonSerializerContext;

/// <summary>Compact, since a ticket's payload travels in every relay Hello.</summary>
[JsonSerializable(typeof(RelayTicketService.Payload))]
internal sealed partial class RelayTicketJsonContext : JsonSerializerContext;
