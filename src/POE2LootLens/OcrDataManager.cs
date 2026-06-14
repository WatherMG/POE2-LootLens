using System.Net.Http;

namespace Poe2LootLens;

internal sealed record OcrDataStatus(bool Success, string Message, string Language);

internal static class OcrDataManager
{
    private const long MinimumModelBytes = 500_000;

    public static Task<OcrDataStatus> EnsurePriceAsync(
        HttpClient http,
        string gameLanguage,
        CancellationToken cancellationToken = default) =>
        EnsureLanguagesAsync(
            http,
            [OcrScanner.ResolvePriceOcrLanguage(gameLanguage)],
            cancellationToken);

    public static Task<OcrDataStatus> EnsureRumorAsync(
        HttpClient http,
        string rumorOcrLanguage,
        CancellationToken cancellationToken = default) =>
        EnsureLanguagesAsync(
            http,
            RumorScanner.ResolveRumorOcrLanguages(rumorOcrLanguage)
                .Split('+', StringSplitOptions.RemoveEmptyEntries),
            cancellationToken);

    private static async Task<OcrDataStatus> EnsureLanguagesAsync(
        HttpClient http,
        IReadOnlyList<string> requestedLanguages,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> required = requestedLanguages
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string directory = Path.Combine(AppContext.BaseDirectory, "tessdata");

        try
        {
            Directory.CreateDirectory(directory);
            bool downloaded = false;
            foreach (string language in required)
            {
                string targetPath = Path.Combine(directory, $"{language}.traineddata");
                if (IsUsable(targetPath))
                    continue;

                await DownloadModelAsync(http, language, targetPath, cancellationToken);
                downloaded = true;
            }

            string modelList = string.Join("+", required);
            return new OcrDataStatus(
                true,
                downloaded ? "OCR-модели загружены" : "OCR-модели готовы",
                modelList);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new OcrDataStatus(
                false,
                $"Не удалось подготовить OCR-модели: {exception.Message}",
                string.Join("+", required));
        }
    }

    internal static IReadOnlyList<string> ResolveRequiredLanguages(
        string? gameLanguage,
        string? rumorOcrLanguage)
    {
        var result = new List<string>();
        void Add(string language)
        {
            if (!result.Contains(language, StringComparer.Ordinal))
                result.Add(language);
        }

        Add(OcrScanner.ResolvePriceOcrLanguage(gameLanguage));
        foreach (string language in RumorScanner.ResolveRumorOcrLanguages(rumorOcrLanguage)
                     .Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            Add(language);
        }
        return result;
    }

    private static async Task DownloadModelAsync(
        HttpClient http,
        string language,
        string targetPath,
        CancellationToken cancellationToken)
    {
        string url = language switch
        {
            "rus" => "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/rus.traineddata",
            "eng" => "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/eng.traineddata",
            _ => throw new InvalidOperationException($"Unsupported OCR language: {language}"),
        };

        string temporaryPath = targetPath + ".download";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "PoE2LootLens/0.9");
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            if (!IsUsable(temporaryPath))
                throw new InvalidDataException($"Downloaded OCR model is incomplete: {language}.traineddata");

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static bool IsUsable(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= MinimumModelBytes;
        }
        catch
        {
            return false;
        }
    }
}
