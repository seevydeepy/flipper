using Windows.Security.Credentials;

namespace Flipper.App.Services;

/// <summary>User-scoped Windows Credential Locker entry; never part of settings or a score catalogue.</summary>
internal static class JevApiKeyStore
{
    private const string Resource = "Carousel.TypeSafe.Jev";
    private const string UserName = "api-key";
    private const int NotFound = unchecked((int)0x80070490);

    public static string? Load()
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, UserName);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception exception) when (exception.HResult == NotFound)
        {
            return null;
        }
    }

    public static void Save(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("An API key is required", nameof(key));
        new PasswordVault().Add(new PasswordCredential(Resource, UserName, key.Trim()));
    }

    public static void Remove()
    {
        var vault = new PasswordVault();
        try { vault.Remove(vault.Retrieve(Resource, UserName)); }
        catch (Exception exception) when (exception.HResult == NotFound) { }
    }
}
