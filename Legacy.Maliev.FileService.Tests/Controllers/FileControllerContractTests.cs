using System.Reflection;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Api.Controllers;
using Legacy.Maliev.FileService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    private static void AssertMethodContract(string name, Type methodAttribute, string permission)
    {
        var method = typeof(UploadsController).GetMethod(name) ?? typeof(SignedUrlController).GetMethod(name);
        Assert.NotNull(method);
        Assert.NotNull(method.GetCustomAttributes().SingleOrDefault(attribute => attribute.GetType() == methodAttribute));
        var permissionAttribute = Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(permission, permissionAttribute.Permission);
    }
}
