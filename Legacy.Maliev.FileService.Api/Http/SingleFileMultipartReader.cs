using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Net.Http.Headers;

namespace Legacy.Maliev.FileService.Api.Http;

/// <summary>Reads one unbuffered multipart file section and defers the extra-section check until consumption.</summary>
public sealed class SingleFileMultipartReader : IInstantQuoteMultipartReader
{
    private const int MaximumBoundaryLength = 128;
    private const int MaximumHeaderLength = 16 * 1024;
    private const int MaximumHeaderCount = 16;

    /// <inheritdoc />
    public async Task<InstantQuoteMultipartFile> ReadSingleAsync(
        HttpRequest request,
        string requiredPartName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(requiredPartName);

        try
        {
            var boundary = GetBoundary(request.ContentType);
            var reader = new MultipartReader(boundary, request.Body)
            {
                HeadersCountLimit = MaximumHeaderCount,
                HeadersLengthLimit = MaximumHeaderLength,
            };
            var section = await reader.ReadNextSectionAsync(cancellationToken);
            if (section is null ||
                !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(HeaderUtilities.RemoveQuotes(disposition.Name).Value, requiredPartName, StringComparison.Ordinal) ||
                disposition.FileName.Value is null && disposition.FileNameStar.Value is null)
            {
                throw new InstantQuoteValidationException("Exactly one file section with the required name is expected.");
            }

            var fileName = HeaderUtilities.RemoveQuotes(
                disposition.FileNameStar.Value is null ? disposition.FileName : disposition.FileNameStar).Value;
            var normalized = InstantQuoteFilePolicy.NormalizeFileMetadata(
                fileName ?? string.Empty,
                section.ContentType ?? string.Empty);
            var body = new BoundedHashingReadStream(section.Body);

            return new InstantQuoteMultipartFile(
                body,
                normalized.Metadata,
                async completionCancellationToken =>
                {
                    if (!body.IsComplete)
                    {
                        throw new InstantQuoteValidationException("The uploaded file was not fully consumed.");
                    }

                    if (await reader.ReadNextSectionAsync(completionCancellationToken) is not null)
                    {
                        throw new InstantQuoteValidationException("Additional multipart sections are not allowed.");
                    }
                });
        }
        catch (InstantQuoteContractException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw new InstantQuoteValidationException($"Multipart request is invalid: {exception.Message}");
        }
    }

    private static string GetBoundary(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType) ||
            !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new InstantQuoteValidationException("Content-Type must be multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > MaximumBoundaryLength ||
            boundary.Any(character => character is < ' ' or > '~'))
        {
            throw new InstantQuoteValidationException("Multipart boundary is missing or invalid.");
        }

        return boundary;
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
