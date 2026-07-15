using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class FileModelCompatibilityTests
{
    [Fact]
    public void UploadMapping_PreservesLegacyTableAndColumns()
    {
        var options = new DbContextOptionsBuilder<FileDbContext>().UseNpgsql("Host=localhost;Database=model").Options;
        using var context = new FileDbContext(options);
        var entity = context.Model.FindEntityType(typeof(Upload))!;
        var table = StoreObjectIdentifier.Table("Upload", null);

        Assert.Equal("Upload", entity.GetTableName());
        Assert.Equal("ID", entity.FindProperty(nameof(Upload.Id))!.GetColumnName(table));
        Assert.Equal(50, entity.FindProperty(nameof(Upload.Bucket))!.GetMaxLength());
        Assert.Equal(50, entity.FindProperty(nameof(Upload.ContentType))!.GetMaxLength());
        Assert.Null(entity.FindProperty(nameof(Upload.Name))!.GetMaxLength());
        Assert.Null(entity.FindProperty("xmin"));
    }
}
