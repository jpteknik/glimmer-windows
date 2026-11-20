// Ported from Packages/GlimmerKit/Sources/GlimmerAI/SecretStore.swift
namespace Glimmer.AI;

/// One secret (an API key). On the Mac it is a keychain item; on Windows the app supplies one
/// that keeps it in Windows Credential Manager (current user, target "Glimmer/&lt;service slug&gt;"). This
/// library only defines the operations and the stores that need no operating system: memory, and the environment.
public interface ISecretStore
{
    string? Read();
    void Write(string value);
    void Delete();
}

/// The secret could not be read or written by the platform's store. The Windows store throws
/// this with the system's own words, as `KeychainError` did ("Keychain error 123: …").
public sealed class SecretStoreException : Exception
{
    public SecretStoreException(string message, Exception? inner = null) : base(message, inner) { }
}

/// For tests and previews.
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly object gate = new();
    private string? value;

    public InMemorySecretStore(string? value = null) { this.value = value; }

    public string? Read() { lock (gate) return value; }
    public void Write(string value) { lock (gate) this.value = value; }
    public void Delete() { lock (gate) value = null; }
}

/// A key read from an environment variable, for debug use: the Mac's debug builds read
/// `OPENROUTER_API_KEY` when the keychain has none, so test runs never store a key.
/// Read-only: writing or deleting says where the key comes from.
public sealed class EnvironmentSecretStore : ISecretStore
{
    public string Variable { get; }
    private readonly Func<string, string?> environment;

    public EnvironmentSecretStore(string variable = "OPENROUTER_API_KEY", Func<string, string?>? environment = null)
    {
        Variable = variable;
        this.environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public string? Read()
    {
        var value = environment(Variable);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public void Write(string value) =>
        throw new SecretStoreException($"The key comes from the environment variable {Variable}; change it there.");

    public void Delete() =>
        throw new SecretStoreException($"The key comes from the environment variable {Variable}; remove it there.");
}

/// A store with a fallback for reading: the first one's key when it has one, otherwise the
/// second's. Writes and deletes go to the first. (`AIController.openRouterKey`: the keychain,
/// then `OPENROUTER_API_KEY`.)
public sealed class FallbackSecretStore : ISecretStore
{
    private readonly ISecretStore primary;
    private readonly ISecretStore fallback;

    public FallbackSecretStore(ISecretStore primary, ISecretStore fallback)
    {
        this.primary = primary;
        this.fallback = fallback;
    }

    public string? Read()
    {
        var key = primary.Read();
        return string.IsNullOrEmpty(key) ? fallback.Read() : key;
    }

    public void Write(string value) => primary.Write(value);
    public void Delete() => primary.Delete();
}

/// Keys kept in memory for one run of a test copy (the Mac's `-ai.noKeychain YES`, debug builds only).
public sealed class MemorySecrets
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> items = new();

    public string? Get(string account) { lock (gate) return items.TryGetValue(account, out var v) ? v : null; }

    public void Set(string account, string? value)
    {
        lock (gate)
        {
            if (value is null) items.Remove(account); else items[account] = value;
        }
    }
}

public sealed class MemorySecretStore : ISecretStore
{
    private readonly MemorySecrets secrets;
    private readonly string account;

    public MemorySecretStore(MemorySecrets secrets, string account)
    {
        this.secrets = secrets;
        this.account = account;
    }

    public string? Read() => secrets.Get(account);
    public void Write(string value) => secrets.Set(account, value);
    public void Delete() => secrets.Set(account, null);
}
