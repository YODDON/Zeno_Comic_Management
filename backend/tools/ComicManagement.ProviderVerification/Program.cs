using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using ComicManagement.ProviderVerification;
using Npgsql;

try
{
    return args is ["cloudinary", ..]
        ? await VerifyCloudinaryAsync()
        : args is ["sepay", var scenario]
            ? await VerifySePayAsync(SePayVerificationScenarioParser.Parse(scenario))
            : Usage();
}
catch (VerificationConfigurationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"Provider verification failed closed ({exception.GetType().Name}).");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project backend/tools/ComicManagement.ProviderVerification -- " +
        "cloudinary|sepay <success-replay|unknown-order|amount-mismatch|late-payment|invalid-signature|retry>");
    return 2;
}

static async Task<int> VerifyCloudinaryAsync()
{
    var cloudName = VerificationConfiguration.Required("Q02_CLOUDINARY_CLOUD_NAME");
    var apiKey = VerificationConfiguration.Required("Q02_CLOUDINARY_API_KEY");
    var apiSecret = VerificationConfiguration.Required("Q02_CLOUDINARY_API_SECRET");
    var prefix = VerificationConfiguration.CloudinaryPrefix(
        Environment.GetEnvironmentVariable("Q02_CLOUDINARY_PREFIX"));
    var timeout = VerificationConfiguration.HttpTimeout(
        Environment.GetEnvironmentVariable("Q02_HTTP_TIMEOUT_SECONDS"));
    var publicId = $"{prefix}/{Guid.NewGuid():N}";
    var cloudinary = new Cloudinary(new Account(cloudName, apiKey, apiSecret));
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = timeout,
    };

    var probes = new Dictionary<string, ProviderProbeResult>(StringComparer.Ordinal);
    var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
    var uploadSucceeded = false;
    string? cleanupResult = null;
    string? providerFailure = null;
    try
    {
        await using var image = new MemoryStream(OnePixelPng());
        var uploaded = await cloudinary.UploadAsync(new ImageUploadParams
        {
            File = new FileDescription("q02.png", image),
            PublicId = publicId,
            Type = "authenticated",
            Overwrite = false,
            UseFilename = false,
            DiscardOriginalFilename = true,
        }).WaitAsync(timeout);
        uploadSucceeded = true;
        checks["authenticatedUpload"] =
            string.Equals(uploaded.PublicId, publicId, StringComparison.Ordinal) &&
            string.Equals(uploaded.Type, "authenticated", StringComparison.Ordinal) &&
            uploaded.Width > 0 && uploaded.Height > 0;

        var unsignedUrl = cloudinary.Api.UrlImgUp.Secure(true).Type("authenticated").BuildUrl(publicId);
        var signedUrl = cloudinary.Api.UrlImgUp.Secure(true).Signed(true).Type("authenticated").BuildUrl(publicId);
        var guessedUrl = cloudinary.Api.UrlImgUp.Secure(true).Type("authenticated")
            .BuildUrl($"{prefix}/{Guid.NewGuid():N}");

        probes["unsigned"] = await ProviderProbe.ProbeAsync(http, unsignedUrl, expectSuccess: false);
        probes["guessed"] = await ProviderProbe.ProbeAsync(http, guessedUrl, expectSuccess: false);
        probes["tampered"] = await ProviderProbe.ProbeAsync(
            http,
            TamperSignature(signedUrl),
            expectSuccess: false);
        probes["signedInternal"] = await ProviderProbe.ProbeAsync(http, signedUrl, expectSuccess: true);
    }
    catch (Exception exception) when (exception is not VerificationConfigurationException)
    {
        providerFailure = ProviderProbe.ProviderUnavailable;
        checks["providerAvailable"] = false;
    }
    finally
    {
        if (uploadSucceeded)
        {
            try
            {
                var deletion = await cloudinary.DestroyAsync(new DeletionParams(publicId)
                {
                    Type = "authenticated",
                    ResourceType = ResourceType.Image,
                    Invalidate = true,
                }).WaitAsync(timeout);
                cleanupResult = deletion.Result;
                checks["cleanupAccepted"] = cleanupResult == "ok";
            }
            catch (Exception exception) when (exception is not VerificationConfigurationException)
            {
                providerFailure ??= ProviderProbe.ProviderUnavailable;
                checks["cleanupAccepted"] = false;
            }
        }
    }

    var passed = CloudinaryVerification.IsPassed(checks, probes, providerFailure);
    await EvidenceWriter.WriteAsync(new
    {
        evidenceRunId = Guid.NewGuid(),
        provider = "Cloudinary",
        verifiedAt = DateTimeOffset.UtcNow,
        selectedDelivery = "server-side authenticated asset proxy",
        providerUrlExposedToReader = false,
        checks,
        probes,
        providerFailure,
        cleanupResult,
        passed,
        limitations = new[]
        {
            "This probe does not verify Cloudinary token/cookie access or a browser entitlement flow.",
            "Q02 remains blocked until the Reader authorization/revocation drill is captured on the project account.",
        },
    });
    return passed ? 0 : 1;
}

static async Task<int> VerifySePayAsync(SePayVerificationScenario scenario)
{
    var connectionString = VerificationConfiguration.Required("Q02_POSTGRES_CONNECTION_STRING");
    var providerTransactionId = VerificationConfiguration.Required(
        "Q02_SEPAY_PROVIDER_TRANSACTION_ID");
    var orderCode = Environment.GetEnvironmentVariable("Q02_SEPAY_ORDER_CODE")?.Trim();
    var observedHttpStatus = VerificationConfiguration.HttpStatus("Q02_SEPAY_HTTP_STATUS");
    int? initialRetryHttpStatus = scenario == SePayVerificationScenario.Retry
        ? VerificationConfiguration.HttpStatus("Q02_SEPAY_INITIAL_HTTP_STATUS")
        : null;
    if (scenario != SePayVerificationScenario.UnknownOrder && string.IsNullOrWhiteSpace(orderCode))
    {
        throw new VerificationConfigurationException(
            "Missing required environment variable Q02_SEPAY_ORDER_CODE.");
    }

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    var observation = await SePayDatabaseVerifier.ObserveAsync(
        connection,
        providerTransactionId,
        orderCode);
    var result = SePayDatabaseVerifier.Verify(
        scenario,
        observation,
        observedHttpStatus,
        initialRetryHttpStatus);

    await EvidenceWriter.WriteAsync(new
    {
        evidenceRunId = Guid.NewGuid(),
        provider = "SePay Test mode",
        scenario = SePayVerificationScenarioParser.ToContractName(scenario),
        verifiedAt = DateTimeOffset.UtcNow,
        result.Outcome,
        result.Checks,
        result.Observed,
        observedHttpStatus,
        initialRetryHttpStatus,
        result.Passed,
        identifiersPersisted = false,
        limitations = new[]
        {
            "Run after the named scenario traverses the deployed webhook controller and production workflow.",
            "Local fixture scenarios are supplementary; project Test-mode evidence is still required.",
        },
    });
    return result.Passed ? 0 : 1;
}

static string TamperSignature(string url)
{
    var marker = "/s--";
    var start = url.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0 || start + marker.Length >= url.Length)
    {
        throw new VerificationConfigurationException(
            "Cloudinary did not produce a signed delivery URL.");
    }

    var index = start + marker.Length;
    var replacement = url[index] == 'A' ? 'B' : 'A';
    return url[..index] + replacement + url[(index + 1)..];
}

static byte[] OnePixelPng() => Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
