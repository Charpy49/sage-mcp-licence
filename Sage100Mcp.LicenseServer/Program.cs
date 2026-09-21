using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Sage100Mcp.LicenseServer.Licensing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<LicenseRepository>();
builder.Services.AddSingleton<ReleaseRepository>();

var app = builder.Build();

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
    ValidateRequest request, LicenseRepository repo, ReleaseRepository releases, HttpContext http,
    CancellationToken ct) =>
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

    await repo.RecordValidationAsync(license.Id, http.Connection.RemoteIpAddress?.ToString(),
        request.InstalledVersion, request.Transport, ct);

    var manifest = UpdateResolver.Resolve(await releases.ListAsync(ct), license.UpdateChannel, license.PinnedVersion);

    return TypedResults.Ok(new ValidateResponse(true, ClientName: license.ClientName,
        ExpiresAtUtc: license.ExpiresAtUtc, AllowedTools: license.AllowedTools, Update: manifest));
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

admin.MapPost("/", async Task<Created<CreateLicenseResponse>> (
    CreateLicenseRequest request, LicenseRepository repo, CancellationToken ct) =>
{
    var key = LicenseKeyGenerator.Generate();
    var record = await repo.CreateAsync(request.ClientName, key, request.ExpiresAtUtc, request.AllowedTools, ct);
    var response = new CreateLicenseResponse(record.Id, key, record.ClientName, record.ExpiresAtUtc, record.AllowedTools);
    return TypedResults.Created($"/api/admin/licenses/{record.Id}", response);
});

admin.MapGet("/", async (LicenseRepository repo, CancellationToken ct) =>
    (await repo.ListAsync(ct)).Select(LicenseSummary.From));

admin.MapGet("/{id}", async Task<Results<Ok<LicenseSummary>, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
{
    var record = await repo.FindByIdAsync(id, ct);
    return record is null ? TypedResults.NotFound() : TypedResults.Ok(LicenseSummary.From(record));
});

admin.MapPut("/{id}", async Task<Results<Ok<LicenseSummary>, NotFound>> (
    string id, UpdateLicenseRequest request, LicenseRepository repo, CancellationToken ct) =>
{
    var updated = await repo.UpdateAsync(id, request.ClientName, request.ExpiresAtUtc, request.IsRevoked,
        request.AllowedTools, request.ClearAllowedTools ?? false, ct);
    if (!updated) return TypedResults.NotFound();
    var record = await repo.FindByIdAsync(id, ct);
    return TypedResults.Ok(LicenseSummary.From(record!));
});

admin.MapPost("/{id}/revoke", async Task<Results<Ok<LicenseSummary>, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
{
    var updated = await repo.UpdateAsync(id, null, null, isRevoked: true, null, false, ct);
    if (!updated) return TypedResults.NotFound();
    var record = await repo.FindByIdAsync(id, ct);
    return TypedResults.Ok(LicenseSummary.From(record!));
});

admin.MapPost("/{id}/unrevoke", async Task<Results<Ok<LicenseSummary>, NotFound>> (
    string id, LicenseRepository repo, CancellationToken ct) =>
{
    var updated = await repo.UpdateAsync(id, null, null, isRevoked: false, null, false, ct);
    if (!updated) return TypedResults.NotFound();
    var record = await repo.FindByIdAsync(id, ct);
    return TypedResults.Ok(LicenseSummary.From(record!));
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
    return TypedResults.Ok(LicenseSummary.From(record!));
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

static bool FixedTimeEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
