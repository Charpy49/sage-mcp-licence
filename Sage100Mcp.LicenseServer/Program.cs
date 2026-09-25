using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Sage100Mcp.LicenseServer.Licensing;

// Outil ponctuel : génère la paire de clés de signature des licences, puis s'arrête.
//   dotnet run -- generate-signing-key
if (args is ["generate-signing-key"])
{
    var (privateKey, publicKeyPem) = LicenseSigner.GenerateKeyPair();
    Console.WriteLine("Clé PRIVÉE — à mettre dans Licensing__SigningKey du serveur de licences, et nulle part ailleurs :");
    Console.WriteLine(privateKey);
    Console.WriteLine();
    Console.WriteLine("Clé PUBLIQUE — à recopier dans PublicKeyPem de Sage100Mcp/Licensing/LicenseToken.cs :");
    Console.WriteLine(publicKeyPem);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<LicenseRepository>();
builder.Services.AddSingleton<ReleaseRepository>();
builder.Services.AddSingleton<LicenseSigner>();

var app = builder.Build();

// Instanciée dès le démarrage : une clé de signature absente ou invalide doit empêcher le service de
// démarrer, pas faire échouer la première validation de licence d'un client.
app.Services.GetRequiredService<LicenseSigner>();

// Transition vers l'activation par poste : tant que des clients antérieurs à la 1.3.0 sont en service,
// une validation sans empreinte de poste est acceptée (sans jeton signé). À passer à true une fois la
// 1.3.0 déclarée plancher de version — sinon rester sur une ancienne version suffit à contourner le contrôle.
var requireMachineId = app.Configuration.GetValue("Licensing:RequireMachineId", false);

// Durée pendant laquelle un jeton reste accepté hors connexion par le client.
var offlineGraceDays = app.Configuration.GetValue("Licensing:OfflineGraceDays", 7);

// --- Interface d'administration (wwwroot/admin) ---
// Page statique sans aucun secret : elle appelle /api/admin/* avec la clé saisie par l'administrateur,
// gardée en sessionStorage le temps de l'onglet. En-têtes stricts : aucun script ni style extérieur,
// pas d'intégration dans un cadre (détournement de clic), pas de mise en cache.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin"))
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
            "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        headers.XFrameOptions = "DENY";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-store";
    }
    await next();
});
// UseDefaultFiles redirige lui-même /admin vers /admin/ puis sert index.html.
app.UseDefaultFiles();
app.UseStaticFiles();

// La valeur de remplacement de appsettings.json est publique (le fichier est versionné et le dépôt
// est sur GitHub) : la refuser explicitement, sinon un déploiement qui oublie de définir
// Licensing__AdminApiKey démarre avec une clé d'administration connue de tous.
const string adminKeyPlaceholder = "CHANGE_ME_avant_deploiement";

var adminKey = app.Configuration["Licensing:AdminApiKey"];
if (string.IsNullOrWhiteSpace(adminKey) || adminKey == adminKeyPlaceholder)
{
    throw new InvalidOperationException(
        "Licensing:AdminApiKey doit être configuré avec une vraie clé secrète (variable d'environnement " +
        $"Licensing__AdminApiKey, ou appsettings.json hors dépôt). Valeur actuelle : {(string.IsNullOrWhiteSpace(adminKey) ? "absente" : "valeur de remplacement")}.");
}

// --- Endpoint public, appelé par les serveurs MCP déployés chez les clients ---
app.MapPost("/api/license/validate", async Task<Ok<ValidateResponse>> (
    ValidateRequest request, LicenseRepository repo, ReleaseRepository releases, LicenseSigner signer,
    HttpContext http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.LicenseKey))
    {
        return TypedResults.Ok(new ValidateResponse(false, Reason: "missing_key"));
    }

    var license = await repo.FindByKeyAsync(request.LicenseKey, ct);
    if (license is null)
    {
        return TypedResults.Ok(new ValidateResponse(false, Reason: "unknown_key"));
    }
    if (license.IsRevoked)
    {
        return TypedResults.Ok(new ValidateResponse(false, Reason: "revoked", ClientName: license.ClientName));
    }
    if (license.ExpiresAtUtc < DateTimeOffset.UtcNow)
    {
        return TypedResults.Ok(new ValidateResponse(false, Reason: "expired", ClientName: license.ClientName,
            ExpiresAtUtc: license.ExpiresAtUtc));
    }

    var machineId = request.MachineId?.Trim();
    if (string.IsNullOrEmpty(machineId))
    {
        // Client antérieur à la 1.3.0 : il ne sait ni s'identifier ni vérifier un jeton.
        if (requireMachineId)
        {
            return TypedResults.Ok(new ValidateResponse(false, Reason: "missing_machine_id",
                ClientName: license.ClientName));
        }
    }
    else
    {
        if (machineId.Length > 128 || request.Nonce?.Length > 128)
        {
            return TypedResults.Ok(new ValidateResponse(false, Reason: "invalid_request"));
        }

        var machineName = request.MachineName?.Trim() is { Length: > 0 } name ? name[..Math.Min(name.Length, 64)] : null;
        var (accepted, active) = await repo.ActivateAsync(license, machineId, machineName, ct);
        if (!accepted)
        {
            return TypedResults.Ok(new ValidateResponse(false, Reason: "machine_limit", ClientName: license.ClientName,
                ActivatedMachines: active.Select(a => a.MachineName ?? "(poste sans nom)").ToList(),
                MaxMachines: license.MaxMachines));
        }
    }

    await repo.RecordValidationAsync(license.Id, http.Connection.RemoteIpAddress?.ToString(),
        request.InstalledVersion, request.Transport, ct);

    var manifest = UpdateResolver.Resolve(await releases.ListAsync(ct), license.UpdateChannel, license.PinnedVersion);

    string? token = null, signature = null;
    if (!string.IsNullOrEmpty(machineId))
    {
        var now = DateTimeOffset.UtcNow;
        (token, signature) = signer.Sign(new LicenseToken(1, license.KeyHash, machineId, license.ClientName,
            license.ExpiresAtUtc, license.AllowedTools, now, now.AddDays(offlineGraceDays), request.Nonce));
    }

    return TypedResults.Ok(new ValidateResponse(true, ClientName: license.ClientName,
        ExpiresAtUtc: license.ExpiresAtUtc, AllowedTools: license.AllowedTools, Update: manifest,
        Token: token, TokenSignature: signature));
});

// --- Endpoint public dédié au shim de mise à jour ---
// Séparé de la validation de licence : le shim s'exécute avant le serveur MCP et n'a besoin
// que du manifeste. Une licence expirée reçoit quand même le manifeste (sinon un client dont
// l'abonnement a lapsé ne pourrait plus jamais recevoir de correctif) ; une licence révoquée, non.
app.MapPost("/api/version/check", async Task<Results<Ok<UpdateManifest>, UnauthorizedHttpResult>> (
    ValidateRequest request, LicenseRepository repo, ReleaseRepository releases, HttpContext http,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.LicenseKey)) return TypedResults.Unauthorized();

    var license = await repo.FindByKeyAsync(request.LicenseKey, ct);
    if (license is null || license.IsRevoked) return TypedResults.Unauthorized();

    await repo.RecordValidationAsync(license.Id, http.Connection.RemoteIpAddress?.ToString(),
        request.InstalledVersion, request.Transport, ct);

    return TypedResults.Ok(
        UpdateResolver.Resolve(await releases.ListAsync(ct), license.UpdateChannel, license.PinnedVersion));
});

// --- Endpoints d'administration, protégés par une clé partagée ---
var admin = app.MapGroup("/api/admin/licenses").AddEndpointFilter(AdminKeyFilter);

admin.MapPost("/", async Task<Results<Created<CreateLicenseResponse>, BadRequest<string>>> (
    CreateLicenseRequest request, LicenseRepository repo, CancellationToken ct) =>
{
    if (request.MaxMachines is < 1) return TypedResults.BadRequest("MaxMachines doit valoir au moins 1.");

    var key = LicenseKeyGenerator.Generate();
    var record = await repo.CreateAsync(request.ClientName, key, request.ExpiresAtUtc, request.AllowedTools,
        request.MaxMachines ?? 1, ct);
    var response = new CreateLicenseResponse(record.Id, key, record.ClientName, record.ExpiresAtUtc, record.AllowedTools,
        record.MaxMachines);
    return TypedResults.Created($"/api/admin/licenses/{record.Id}", response);
});

admin.MapGet("/", async (LicenseRepository repo, CancellationToken ct) =>
{
    var summaries = new List<LicenseSummary>();
    foreach (var record in await repo.ListAsync(ct))
    {
        summaries.Add(await Summarize(repo, record, ct));
    }
    return summaries;
});

admin.MapGet("/{id}", async Task<Results<Ok<LicenseSummary>, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
{
    var record = await repo.FindByIdAsync(id, ct);
    return record is null ? TypedResults.NotFound() : TypedResults.Ok(await Summarize(repo, record, ct));
});

admin.MapPut("/{id}", async Task<Results<Ok<LicenseSummary>, NotFound, BadRequest<string>>> (
    string id, UpdateLicenseRequest request, LicenseRepository repo, CancellationToken ct) =>
{
    if (request.MaxMachines is < 1) return TypedResults.BadRequest("MaxMachines doit valoir au moins 1.");

    var updated = await repo.UpdateAsync(id, request.ClientName, request.ExpiresAtUtc, request.IsRevoked,
        request.AllowedTools, request.ClearAllowedTools ?? false, request.MaxMachines, ct);
    if (!updated) return TypedResults.NotFound();
    var record = await repo.FindByIdAsync(id, ct);
    return TypedResults.Ok(await Summarize(repo, record!, ct));
});

admin.MapPost("/{id}/revoke", async Task<Results<Ok<LicenseSummary>, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
{
    var updated = await repo.UpdateAsync(id, null, null, isRevoked: true, null, false, null, ct);
    if (!updated) return TypedResults.NotFound();
    var record = await repo.FindByIdAsync(id, ct);
    return TypedResults.Ok(await Summarize(repo, record!, ct));
});

admin.MapPost("/{id}/unrevoke", async Task<Results<Ok<LicenseSummary>, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
{
    var updated = await repo.UpdateAsync(id, null, null, isRevoked: false, null, false, null, ct);
    if (!updated) return TypedResults.NotFound();
    var record = await repo.FindByIdAsync(id, ct);
    return TypedResults.Ok(await Summarize(repo, record!, ct));
});

// Postes ayant activé la licence. Libérer un poste (changement de PC, réinstallation de Windows,
// migration de serveur) rend sa place : le prochain poste qui se présente la prend.
admin.MapGet("/{id}/machines", async Task<Results<Ok<IReadOnlyList<ActivationRecord>>, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
{
    var record = await repo.FindByIdAsync(id, ct);
    return record is null ? TypedResults.NotFound() : TypedResults.Ok(await repo.ListActivationsAsync(record, ct));
});

admin.MapDelete("/{id}/machines/{machineId}", async Task<Results<NoContent, NotFound>> (
    string id, string machineId, LicenseRepository repo, CancellationToken ct) =>
    await repo.DeactivateAsync(id, machineId, ct) > 0 ? TypedResults.NoContent() : TypedResults.NotFound());

admin.MapDelete("/{id}/machines", async Task<Results<Ok<LicenseSummary>, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
{
    var record = await repo.FindByIdAsync(id, ct);
    if (record is null) return TypedResults.NotFound();
    await repo.DeactivateAsync(id, null, ct);
    return TypedResults.Ok(await Summarize(repo, record, ct));
});

admin.MapDelete("/{id}", async Task<Results<NoContent, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
    await repo.DeleteAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound());

// Politique de mise à jour d'un client : canal suivi et épinglage de version.
admin.MapPut("/{id}/update-policy", async Task<Results<Ok<LicenseSummary>, NotFound, BadRequest<string>>> (
    string id, UpdatePolicyRequest request, LicenseRepository repo, ReleaseRepository releases,
    CancellationToken ct) =>
{
    if (request.PinnedVersion is { } pin && !string.IsNullOrWhiteSpace(pin))
    {
        if (!UpdateResolver.IsValidVersion(pin))
            return TypedResults.BadRequest($"Version d'épinglage invalide : '{pin}'. Format attendu : 1.3.2.");
        if (await releases.FindAsync(pin, ct) is null)
            return TypedResults.BadRequest($"Aucune version publiée '{pin}' : publiez-la avant d'y épingler un client.");
    }

    var updated = await repo.SetUpdatePolicyAsync(id, request.UpdateChannel, request.PinnedVersion,
        request.ClearPinnedVersion ?? false, ct);
    if (!updated) return TypedResults.NotFound();

    var record = await repo.FindByIdAsync(id, ct);
    return TypedResults.Ok(await Summarize(repo, record!, ct));
});

// --- Administration des versions publiées, même clé partagée ---
var releasesAdmin = app.MapGroup("/api/admin/releases").AddEndpointFilter(AdminKeyFilter);

releasesAdmin.MapPost("/", async Task<Results<Created<ReleaseRecord>, BadRequest<string>, Conflict<string>>> (
    CreateReleaseRequest request, ReleaseRepository repo, CancellationToken ct) =>
{
    if (!UpdateResolver.IsValidVersion(request.Version))
    {
        return TypedResults.BadRequest(
            $"Version invalide : '{request.Version}'. Format attendu Major.Minor.Patch (ex. 1.3.2) ; " +
            "les suffixes de préversion ne sont pas gérés — utilisez un canal dédié.");
    }
    if (string.IsNullOrWhiteSpace(request.DownloadUrl) || string.IsNullOrWhiteSpace(request.Sha256))
    {
        return TypedResults.BadRequest("DownloadUrl et Sha256 sont obligatoires.");
    }
    if (await repo.FindAsync(request.Version, ct) is not null)
    {
        return TypedResults.Conflict($"La version '{request.Version}' est déjà publiée. " +
                                     "Une version publiée est immuable : publiez un nouveau numéro.");
    }

    var record = await repo.CreateAsync(request.Version, request.Channel, request.DownloadUrl, request.Sha256,
        request.Signature, request.Notes, request.IsMinimum, ct);
    return TypedResults.Created($"/api/admin/releases/{record.Version}", record);
});

releasesAdmin.MapGet("/", async (ReleaseRepository repo, CancellationToken ct) => await repo.ListAsync(ct));

releasesAdmin.MapGet("/{version}", async Task<Results<Ok<ReleaseRecord>, NotFound>> (
    string version, ReleaseRepository repo, CancellationToken ct) =>
{
    var record = await repo.FindAsync(version, ct);
    return record is null ? TypedResults.NotFound() : TypedResults.Ok(record);
});

// Coupe-circuit : la version n'est plus servie, les clients redescendent au lancement suivant.
releasesAdmin.MapPost("/{version}/yank", async Task<Results<Ok<ReleaseRecord>, NotFound>> (
    string version, ReleaseRepository repo, CancellationToken ct) =>
    await repo.SetFlagsAsync(version, isYanked: true, isMinimum: null, ct)
        ? TypedResults.Ok((await repo.FindAsync(version, ct))!)
        : TypedResults.NotFound());

releasesAdmin.MapPost("/{version}/unyank", async Task<Results<Ok<ReleaseRecord>, NotFound>> (
    string version, ReleaseRepository repo, CancellationToken ct) =>
    await repo.SetFlagsAsync(version, isYanked: false, isMinimum: null, ct)
        ? TypedResults.Ok((await repo.FindAsync(version, ct))!)
        : TypedResults.NotFound());

// Plancher de version : en dessous, la mise à jour devient obligatoire pour tous les canaux.
releasesAdmin.MapPost("/{version}/minimum", async Task<Results<Ok<ReleaseRecord>, NotFound>> (
    string version, ReleaseRepository repo, CancellationToken ct) =>
    await repo.SetFlagsAsync(version, isYanked: null, isMinimum: true, ct)
        ? TypedResults.Ok((await repo.FindAsync(version, ct))!)
        : TypedResults.NotFound());

// Lève le plancher : la version redevient une mise à jour ordinaire.
releasesAdmin.MapDelete("/{version}/minimum", async Task<Results<Ok<ReleaseRecord>, NotFound>> (
    string version, ReleaseRepository repo, CancellationToken ct) =>
    await repo.SetFlagsAsync(version, isYanked: null, isMinimum: false, ct)
        ? TypedResults.Ok((await repo.FindAsync(version, ct))!)
        : TypedResults.NotFound());

releasesAdmin.MapDelete("/{version}", async Task<Results<NoContent, NotFound>> (
    string version, ReleaseRepository repo, CancellationToken ct) =>
    await repo.DeleteAsync(version, ct) ? TypedResults.NoContent() : TypedResults.NotFound());

app.Run();

async ValueTask<object?> AdminKeyFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    var httpContext = context.HttpContext;
    if (!httpContext.Request.Headers.TryGetValue("X-Admin-Key", out var provided) ||
        !FixedTimeEquals(provided.ToString(), adminKey!))
    {
        return Results.Unauthorized();
    }
    return await next(context);
}

static async Task<LicenseSummary> Summarize(LicenseRepository repo, LicenseRecord record, CancellationToken ct) =>
    LicenseSummary.From(record, await repo.ListActivationsAsync(record, ct));

static bool FixedTimeEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
