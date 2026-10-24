namespace LANCommander.Plugins.Relay.Authentication;

/// <summary>The LANCommander account a relay client's ticket was issued to.</summary>
/// <param name="UserId">The account's id, when the server knows one.</param>
/// <param name="UserName">The account's user name.</param>
public sealed record RelayUser(Guid? UserId, string UserName);
