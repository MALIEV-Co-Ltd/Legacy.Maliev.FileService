using Legacy.Maliev.FileService.Data;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.FileService.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
public sealed class StorageMoveJournalTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task MoveMetadataAsync_ConcurrentSourceRemoval_RejectsZeroRowCommit()
    {
        await using var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        var repository = new UploadRepository(context, TimeProvider.System);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            repository.MoveAsync("private", "absent/source", "private", "clean/destination", default));
    }

    [Fact]
    public async Task BeginAsync_ConcurrentSameOperation_CreatesOneGenerationBoundRow()
    {
        var operationId = Guid.NewGuid();
        await using var firstContext = fixture.CreateContext();
        await firstContext.Database.MigrateAsync();
        await using var secondContext = fixture.CreateContext();
        var first = new StorageMoveJournalRepository(firstContext, TimeProvider.System);
        var second = new StorageMoveJournalRepository(secondContext, TimeProvider.System);

        var results = await Task.WhenAll(
            first.BeginAsync(operationId, true, "private", "quarantine/first", 17, "private", "clean/first", default),
            second.BeginAsync(operationId, true, "private", "quarantine/other", 19, "private", "clean/other", default));

        Assert.Single(results, result => result);
        var row = await firstContext.StorageMoveJournals.AsNoTracking().SingleAsync(move => move.OperationId == operationId);
        Assert.True(row.ScanClean);
        Assert.True(row.SourceGeneration is 17 or 19);
        Assert.Null(row.DestinationGeneration);
        Assert.Equal("SourceObserved", row.State);
    }

    [Fact]
    public async Task CheckpointAsync_UnknownCopyAndReplay_PreservesOriginalEvidence()
    {
        var operationId = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        var journal = new StorageMoveJournalRepository(context, TimeProvider.System);
        Assert.True(await journal.BeginAsync(operationId, true, "private", "quarantine/first", 17,
            "private", "clean/first", default));

        await journal.UnknownAsync(operationId, default);
        Assert.False(await journal.BeginAsync(operationId, true, "private", "quarantine/replacement", 18,
            "private", "clean/first", default));

        var row = await context.StorageMoveJournals.AsNoTracking().SingleAsync(move => move.OperationId == operationId);
        Assert.Equal(17, row.SourceGeneration);
        Assert.Equal("quarantine/first", row.SourceObjectName);
        Assert.Equal("Unknown", row.State);
        Assert.Null(row.DestinationGeneration);
    }

    [Fact]
    public async Task CheckpointAsync_ConfirmedMove_RecordsCopyDeleteAndMetadataStages()
    {
        var operationId = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        var journal = new StorageMoveJournalRepository(context, TimeProvider.System);
        Assert.True(await journal.BeginAsync(operationId, true, "private", "quarantine/first", 17,
            "private", "clean/first", default));

        await journal.CopiedAsync(operationId, 31, default);
        await journal.SourceDeletedAsync(operationId, default);
        await journal.MetadataCommittedAsync(operationId, default);
        await journal.UnknownAsync(operationId, default);

        var row = await context.StorageMoveJournals.AsNoTracking().SingleAsync(move => move.OperationId == operationId);
        Assert.Equal(31, row.DestinationGeneration);
        Assert.Equal("MetadataCommitted", row.State);
    }

    [Fact]
    public async Task CheckpointAsync_UnacknowledgedCopy_CannotCommitMetadata()
    {
        var operationId = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        var journal = new StorageMoveJournalRepository(context, TimeProvider.System);
        Assert.True(await journal.BeginAsync(operationId, true, "private", "quarantine/first", 17,
            "private", "clean/first", default));

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.MetadataCommittedAsync(operationId, default));
    }
}
