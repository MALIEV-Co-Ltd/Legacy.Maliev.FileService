using System.Reflection;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Api.Controllers;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Security.Claims;

namespace Legacy.Maliev.FileService.Tests.Controllers;

public sealed class FileControllerContractTests
{
    [Fact]
    public void UploadsController_PreservesLegacyRouteAndAuthenticatedMethods()
    {
        Assert.Equal("[controller]", typeof(UploadsController).GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(typeof(UploadsController).GetCustomAttribute<AuthorizeAttribute>());
        AssertMethodContract(nameof(UploadsController.UploadAsync), typeof(HttpPostAttribute), FilePermissions.Create);
        AssertMethodContract(nameof(UploadsController.MoveUploadAsync), typeof(HttpPutAttribute), FilePermissions.Update);
        AssertMethodContract(nameof(UploadsController.DeleteUploadAsync), typeof(HttpDeleteAttribute), FilePermissions.Delete);
    }

    [Fact]
    public void SignedUrlController_PreservesLegacyRouteAndAuthentication()
    {
        Assert.Equal("uploads/[controller]", typeof(SignedUrlController).GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(typeof(SignedUrlController).GetCustomAttribute<AuthorizeAttribute>());
        AssertMethodContract(nameof(SignedUrlController.GetSignedUrlAsync), typeof(HttpGetAttribute), FilePermissions.Read);
    }

    [Fact]
    public void UploadResponse_PreservesPascalCaseWireShape()
    {
        var objectProperty = typeof(UploadResultResponse).GetProperty("Object");
        var responseProperties = typeof(UploadObjectResponse).GetProperties().Select(property => property.Name).ToArray();

        Assert.NotNull(objectProperty);
        Assert.Equal(["Bucket", "ObjectName", "Uri"], responseProperties);
    }

    [Fact]
    public void UploadAsync_AcceptsLegacyCompatibleIdempotencyHeader()
    {
        var method = typeof(UploadsController).GetMethod(nameof(UploadsController.UploadAsync))!;
        var parameter = Assert.Single(method.GetParameters(), value =>
            value.GetCustomAttribute<FromHeaderAttribute>()?.Name == "Idempotency-Key");

        Assert.Equal(typeof(string), parameter.ParameterType);
        Assert.True(parameter.HasDefaultValue);
        Assert.Null(parameter.DefaultValue);
    }

    [Fact]
    public async Task KeyedUpload_RequiresStableSignedServicePrincipal()
    {
        var controller = Controller(new StubStore(new(UploadAcquireState.Acquired, "reservation")));
        var result = await controller.UploadAsync("maliev.com", Files(), null, default, "workflow-42");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Theory]
    [InlineData(UploadAcquireState.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(UploadAcquireState.InProgress, StatusCodes.Status409Conflict)]
    [InlineData(UploadAcquireState.Unknown, StatusCodes.Status503ServiceUnavailable)]
    public async Task KeyedUpload_MapsReplayStatesWithoutExecutingService(UploadAcquireState state, int status)
    {
        var service = new Mock<IFileService>(); var controller = Controller(new StubStore(new(state)), service);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client_id", "intranet-service")], "test"));
        var result = await controller.UploadAsync("maliev.com", Files(), null, default, "workflow-42");
        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
        service.Verify(value => value.UploadAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<IUploadFile>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static UploadsController Controller(IUploadIdempotencyStore store, Mock<IFileService>? service = null) =>
        new((service ?? new Mock<IFileService>()).Object, new IdempotentUploadCoordinator(store))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
    private static List<IFormFile> Files() => [new FormFile(new MemoryStream([1]), 0, 1, "files", "part.stl") { Headers = new HeaderDictionary(), ContentType = "model/stl" }];
    private sealed class StubStore(UploadAcquireResult result) : IUploadIdempotencyStore
    {
        public Task<UploadAcquireResult> AcquireAsync(string identity, string fingerprint, CancellationToken cancellationToken) => Task.FromResult(result);
        public Task<bool> RenewAsync(string identity, string reservationId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task CompleteAsync(string identity, string fingerprint, string reservationId, UploadResultResponse response, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkUnknownAsync(string identity, string reservationId, UploadResultResponse? response, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReleaseAsync(string identity, string reservationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static void AssertMethodContract(string name, Type methodAttribute, string permission)
    {
        var method = typeof(UploadsController).GetMethod(name) ?? typeof(SignedUrlController).GetMethod(name);
        Assert.NotNull(method);
        Assert.NotNull(method.GetCustomAttributes().SingleOrDefault(attribute => attribute.GetType() == methodAttribute));
        var permissionAttribute = Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(permission, permissionAttribute.Permission);
    }
}
