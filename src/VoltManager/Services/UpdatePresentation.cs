namespace VoltManager.Services;

internal static class UpdatePresentation
{
    internal const string RateLimited = "Limite richieste GitHub raggiunto. Riprova più tardi.";
    internal const string Offline = "Impossibile contattare GitHub. Verifica la connessione.";
    internal const string NoRelease = "Repository non trovato o nessuna release pubblicata.";
    internal const string UpToDate = "VoltManager è aggiornato.";

    internal static string HttpError(int statusCode) => $"GitHub ha risposto {statusCode}.";
    internal static string NoPublishedRelease(string branch)
        => $"Nessuna release pubblicata. Mostro gli ultimi commit del branch {branch}.";
    internal static string Available(UpdateChannelPolicy policy, string version, bool hasInstaller)
        => hasInstaller
            ? $"Nuova versione {policy.VersionLabel}{version} disponibile."
            : $"Nuova versione {policy.VersionLabel}{version} disponibile, ma l'asset installer non è presente nella release.";
}
