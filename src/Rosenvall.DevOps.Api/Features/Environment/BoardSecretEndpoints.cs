using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace Rosenvall.DevOps.Api;

public static class BoardSecretEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/boards/{boardId:guid}/secrets", (Guid boardId, ClaimsPrincipal user, DevOpsStore store) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            return Results.Ok(store.GetBoardSecrets(boardId));
        });

        api.MapPost("/boards/{boardId:guid}/secrets", async (Guid boardId, CreateBoardSecretRequest request, ClaimsPrincipal user, DevOpsStore store, IRuntimeSecretStore runtimeSecrets, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            var secret = store.PrepareBoardSecretCreate(boardId, request);
            if (secret is null)
            {
                return Results.NotFound();
            }

            var write = await runtimeSecrets.StoreAsync(
                BoardSecretManifestRenderer.SecretName(secret),
                BoardSecretManifestRenderer.SecretData(secret, request.Value),
                BoardSecretManifestRenderer.SecretLabels(secret),
                BoardSecretManifestRenderer.Namespace(configuration),
                cancellationToken);
            if (!write.Succeeded)
            {
                return Results.Problem(write.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            var committed = store.CommitBoardSecretCreate(secret);
            return committed is null
                ? Results.NotFound()
                : Results.Created($"/api/boards/{boardId}/secrets/{committed.Id}", committed);
        });

        api.MapPut("/boards/{boardId:guid}/secrets/{secretId:guid}", async (Guid boardId, Guid secretId, CreateBoardSecretRequest request, ClaimsPrincipal user, DevOpsStore store, IRuntimeSecretStore runtimeSecrets, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            var secret = store.PrepareBoardSecretUpdate(boardId, secretId);
            if (secret is null)
            {
                return Results.NotFound();
            }

            var write = await runtimeSecrets.StoreAsync(
                BoardSecretManifestRenderer.SecretName(secret),
                BoardSecretManifestRenderer.SecretData(secret, request.Value),
                BoardSecretManifestRenderer.SecretLabels(secret),
                BoardSecretManifestRenderer.Namespace(configuration),
                cancellationToken);
            if (!write.Succeeded)
            {
                return Results.Problem(write.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            var committed = store.CommitBoardSecretUpdate(secret);
            return committed is null ? Results.NotFound() : Results.Ok(committed);
        });

        api.MapDelete("/boards/{boardId:guid}/secrets/{secretId:guid}", async (Guid boardId, Guid secretId, ClaimsPrincipal user, DevOpsStore store, IRuntimeSecretStore runtimeSecrets, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            if (!CanMutateBoardRequest(store, boardId, user))
            {
                return BoardMutationForbidden();
            }

            var secret = store.GetBoardSecret(boardId, secretId);
            if (secret is null)
            {
                return Results.NotFound();
            }

            var cleanup = await runtimeSecrets.DeleteAsync(
                BoardSecretManifestRenderer.SecretName(secret),
                BoardSecretManifestRenderer.Namespace(configuration),
                cancellationToken);
            if (!cleanup.Succeeded)
            {
                return Results.Problem(cleanup.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            return store.DeleteBoardSecret(boardId, secretId) ? Results.NoContent() : Results.NotFound();
        });
    }

    private static bool CanMutateBoardRequest(DevOpsStore store, Guid boardId, ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated != true || store.CanMutateBoard(boardId, UserIdentityFromClaims(user).Subject);

    private static IResult BoardMutationForbidden() =>
        Results.Problem("You do not have permission to modify this board.", statusCode: StatusCodes.Status403Forbidden);

    private static UserIdentityRequest UserIdentityFromClaims(ClaimsPrincipal user)
    {
        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ??
            user.FindFirstValue("sub") ??
            "local-dev";
        var email = user.FindFirstValue(ClaimTypes.Email) ??
            user.FindFirstValue("email") ??
            "christopher.rosenvall@gmail.com";
        var displayName = user.FindFirstValue("name") ??
            user.FindFirstValue("preferred_username") ??
            email;
        var avatar = user.FindFirstValue("picture");
        return new UserIdentityRequest(subject, displayName, email, avatar);
    }
}
